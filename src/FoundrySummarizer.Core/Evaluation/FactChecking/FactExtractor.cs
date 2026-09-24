using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FoundrySummarizer.Core.Evaluation.FactChecking;

/// <summary>
/// Finds checkable claims (figures, dates, names) in text using deterministic patterns. The same patterns
/// are used to read the source, so a claim and its source statement are always normalised identically.
/// </summary>
public static class FactExtractor
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private const string NumberPattern = @"(?<num>\d{1,3}(?:,\d{3})+|\d+)(?:\.(?<dec>\d+))?";
    private const string ScalePattern = @"(?:\s?(?<scale>k|mm|m|bn|b|thousand|million|billion)\b)?";

    private static readonly Regex AmountRegex = new(
        @"(?:[$€£¥]\s?|\b(?:USD|EUR|GBP|INR)\s?)" + NumberPattern + ScalePattern,
        Options | RegexOptions.IgnoreCase);

    private static readonly Regex AmountSuffixRegex = new(
        @"(?<![\d.,])" + NumberPattern + ScalePattern + @"\s?(?:USD|EUR|GBP|INR|dollars|euros|pounds)\b",
        Options | RegexOptions.IgnoreCase);

    // Any number in the source, so a summary amount matches however the source wrote it.
    private static readonly Regex AnyNumberRegex = new(@"(?<![\d.,])" + NumberPattern + ScalePattern, Options | RegexOptions.IgnoreCase);

    private static readonly Regex PercentRegex = new(@"(?<![\d.])(?<num>\d+(?:\.\d+)?)\s?(?:%|percent\b|per\s?cent\b)", Options | RegexOptions.IgnoreCase);

    private static readonly Regex MultiplierRegex = new(@"(?<![\w.])(?<num>\d+(?:\.\d+)?)\s?[x×](?!\w)", Options | RegexOptions.IgnoreCase);

    private static readonly Regex DurationRegex = new(
        @"(?<![\w.])(?<num>\d+)\)?[\s-](?:calendar\s|business\s|working\s)?(?<unit>minute|hour|day|week|month|year)s?\b",
        Options | RegexOptions.IgnoreCase);

    // Month names are matched case-sensitively so the modal verb "may" and words like "mar" are not dates.
    private const string MonthPattern =
        @"(?<mon>Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\.?";

    // Ordered most specific first; overlapping later matches are ignored.
    private static readonly (Regex Pattern, Func<Match, IReadOnlyList<DateParts>> Parse)[] DatePatterns =
    {
        (new Regex(@"\b(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?" + MonthPattern + @"(?:,?\s+(?<year>\d{4}))?\b", Options), ParseNamedMonth),
        (new Regex(@"\b" + MonthPattern + @"\s+(?<day>\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s+(?<year>\d{4})\b)?", Options), ParseNamedMonth),
        (new Regex(@"\b" + MonthPattern + @",?\s+(?<year>\d{4})\b", Options), ParseNamedMonth),
        (new Regex(@"\b(?<year>\d{4})-(?<month>\d{1,2})-(?<day>\d{1,2})\b", Options), ParseIso),
        (new Regex(@"\b(?<a>\d{1,2})/(?<b>\d{1,2})/(?<year>\d{4}|\d{2})\b", Options), ParseSlashed),
        (new Regex(@"\bQ(?<quarter>[1-4])\b(?:\s*(?:FY\s?)?(?<year>\d{4}))?", Options), ParseQuarter)
    };

    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;

    private static readonly HashSet<string> NameConnectors = new(StringComparer.Ordinal)
    {
        "of", "and", "&", "for", "de", "van", "von", "der", "del", "la", "le", "du", "da"
    };

    // Capitalised words that carry no factual content on their own (sentence starters, articles).
    private static readonly HashSet<string> NonFactWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "this", "that", "these", "those", "all", "each", "any", "no", "not", "our", "its", "their",
        "stated", "unassigned", "tbd", "none", "unknown"
    };

    // Table columns whose cells hold people or organisations, so every capitalised word in them is checked.
    private static readonly Regex NameColumnHeader = new(@"owner|assignee|responsible|lead|who|part(?:y|ies)|vendor|counterparty", Options | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> MonthWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec"
    };

    private static readonly Regex HeadingLine = new(@"^\s*#{1,6}\s", Options);
    private static readonly Regex TableSeparatorLine = new(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$", Options);
    private static readonly Regex ListMarker = new(@"^\s*(?:[-*+•]|\d+[.)])\s+", Options);

    // Emoji and symbols that decorate the start of a line ("🚩 **Attention Required**:", "⚠️ Risk:").
    private static readonly Regex LeadingDecoration = new(@"^[^\p{L}\p{N}*_(\[""“]+", Options);

    // "**Budget Alignment**: ..." or "Liability Caps: ..." at the start of a line. Labels are wording chosen by
    // the model or the template, not claims about the document, so they are not checked.
    private static readonly Regex LeadingLabel = new(@"^(?:\*\*|__)?(?<label>[^:*_\n]{1,60}?)(?:\*\*|__)?:\s", Options);
    private static readonly Regex Citation = new(@"\[P\d+\]", Options);
    private static readonly Regex SegmentBoundary = new(@"(?<=[.!?:;])\s+|\s[-–—/]\s|[|()\[\]""“”]", Options);
    private static readonly Regex CapitalisedWord = new(@"^[A-Z][A-Za-z'’\-]*$", Options);

    /// <summary>
    /// Extracts the claims in a summary: figures and dates anywhere, and proper names in body text
    /// (headings, table headers and line labels are skipped). Repeated mentions are returned once.
    /// </summary>
    /// <param name="summary">Summary text (Markdown is fine).</param>
    public static IReadOnlyList<SummaryFact> ExtractFromSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return Array.Empty<SummaryFact>();

        var facts = new List<SummaryFact>();
        var masked = new StringBuilder(summary);

        // Dates first, then figures, masking each match so later patterns (and name detection) skip it.
        foreach (var (match, readings) in FindDates(summary))
        {
            facts.Add(new DateFact(match.Value, readings));
            Mask(masked, match);
        }

        foreach (Match m in AmountRegex.Matches(masked.ToString()).Concat(AmountSuffixRegex.Matches(masked.ToString())))
        {
            if (TryParseNumber(m, applyScale: true, out var value))
            {
                facts.Add(new AmountFact(m.Value, value));
                Mask(masked, m);
            }
        }

        foreach (Match m in PercentRegex.Matches(masked.ToString()))
        {
            facts.Add(new PercentageFact(m.Value, ParseDecimal(m.Groups["num"].Value)));
            Mask(masked, m);
        }

        foreach (Match m in MultiplierRegex.Matches(masked.ToString()))
        {
            facts.Add(new MultiplierFact(m.Value, ParseDecimal(m.Groups["num"].Value)));
            Mask(masked, m);
        }

        foreach (Match m in DurationRegex.Matches(masked.ToString()))
        {
            facts.Add(new DurationFact(m.Value, int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture), m.Groups["unit"].Value.ToLowerInvariant()));
            Mask(masked, m);
        }

        facts.AddRange(FindNames(masked.ToString()));

        return facts
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Every numeric value in <paramref name="text"/>, both as written and with any scale word applied.</summary>
    public static IEnumerable<decimal> FindNumbers(string text)
    {
        foreach (Match m in AnyNumberRegex.Matches(text))
        {
            if (TryParseNumber(m, applyScale: false, out var raw)) yield return raw;
            if (m.Groups["scale"].Success && TryParseNumber(m, applyScale: true, out var scaled)) yield return scaled;
        }
    }

    /// <summary>Percentages stated in <paramref name="text"/>.</summary>
    public static IEnumerable<decimal> FindPercentages(string text) =>
        PercentRegex.Matches(text).Select(m => ParseDecimal(m.Groups["num"].Value));

    /// <summary>Multiples such as "2x" stated in <paramref name="text"/>.</summary>
    public static IEnumerable<decimal> FindMultipliers(string text) =>
        MultiplierRegex.Matches(text).Select(m => ParseDecimal(m.Groups["num"].Value));

    /// <summary>Durations stated in <paramref name="text"/>, as <see cref="DurationFact.Key"/> strings.</summary>
    public static IEnumerable<string> FindDurationKeys(string text) =>
        DurationRegex.Matches(text).Select(m =>
            new DurationFact(m.Value, int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture), m.Groups["unit"].Value.ToLowerInvariant()).Key);

    /// <summary>All readings of all dates stated in <paramref name="text"/>.</summary>
    public static IEnumerable<DateParts> FindDateParts(string text) => FindDates(text).SelectMany(d => d.Readings);

    private static IEnumerable<(Match Match, IReadOnlyList<DateParts> Readings)> FindDates(string text)
    {
        var taken = new List<(int Start, int End)>();
        var found = new List<(Match, IReadOnlyList<DateParts>)>();

        foreach (var (pattern, parse) in DatePatterns)
        {
            foreach (Match m in pattern.Matches(text))
            {
                int end = m.Index + m.Length;
                if (taken.Any(t => m.Index < t.End && end > t.Start)) continue;

                var readings = parse(m);
                if (readings.Count == 0) continue;

                taken.Add((m.Index, end));
                found.Add((m, readings));
            }
        }

        return found.OrderBy(f => f.Item1.Index);
    }

    private static IReadOnlyList<DateParts> ParseNamedMonth(Match m)
    {
        var monthToken = m.Groups["mon"].Value;
        int month = Array.FindIndex(MonthNames, n => n.Length > 0 && n.StartsWith(monthToken[..3], StringComparison.OrdinalIgnoreCase)) + 1;
        int? day = m.Groups["day"].Success ? int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture) : null;
        int? year = m.Groups["year"].Success ? int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture) : null;

        if (month == 0 || day is < 1 or > 31) return Array.Empty<DateParts>();
        return new[] { new DateParts(year, month, day, null) };
    }

    private static IReadOnlyList<DateParts> ParseIso(Match m)
    {
        var parts = new DateParts(
            int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["month"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture),
            null);
        return IsValid(parts) ? new[] { parts } : Array.Empty<DateParts>();
    }

    private static IReadOnlyList<DateParts> ParseSlashed(Match m)
    {
        int a = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
        int b = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
        int year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (year < 100) year += 2000;

        // Keep both month/day and day/month readings when both are valid calendar dates.
        return new[] { new DateParts(year, a, b, null), new DateParts(year, b, a, null) }
            .Where(IsValid)
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<DateParts> ParseQuarter(Match m)
    {
        int quarter = int.Parse(m.Groups["quarter"].Value, CultureInfo.InvariantCulture);
        int? year = m.Groups["year"].Success ? int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture) : null;
        return new[] { new DateParts(year, null, null, quarter) };
    }

    private static bool IsValid(DateParts p) =>
        p.Month is >= 1 and <= 12 && p.Day is >= 1 && p.Year is >= 1 &&
        p.Day <= DateTime.DaysInMonth(p.Year.Value, p.Month.Value);

    private static IEnumerable<NameFact> FindNames(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var nameColumns = new HashSet<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            bool isTableHeader = i + 1 < lines.Length && TableSeparatorLine.IsMatch(lines[i + 1]);
            if (isTableHeader)
            {
                nameColumns = SplitCells(line)
                    .Select((cell, index) => (cell, index))
                    .Where(c => NameColumnHeader.IsMatch(c.cell))
                    .Select(c => c.index)
                    .ToHashSet();
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) || HeadingLine.IsMatch(line) || TableSeparatorLine.IsMatch(line))
            {
                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                var cells = SplitCells(line);
                for (int c = 0; c < cells.Count; c++)
                {
                    foreach (var name in FindNamesInText(cells[c], isNameField: nameColumns.Contains(c)))
                    {
                        yield return name;
                    }
                }
                continue;
            }

            nameColumns.Clear();
            line = ListMarker.Replace(line, string.Empty, 1);
            line = LeadingDecoration.Replace(line, string.Empty, 1);
            var label = LeadingLabel.Match(line);
            if (label.Success && IsLabel(label.Groups["label"].Value))
            {
                line = line[label.Length..];
            }

            foreach (var name in FindNamesInText(line, isNameField: false))
            {
                yield return name;
            }
        }
    }

    private static IEnumerable<NameFact> FindNamesInText(string text, bool isNameField)
    {
        var cleaned = Citation.Replace(text.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty), " ");
        return SegmentBoundary.Split(cleaned).SelectMany(segment => FindNamesInSegment(segment, isNameField));
    }

    private static List<string> SplitCells(string tableRow)
    {
        var trimmed = tableRow.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>A line label is a short Title-Case phrase such as "Liability Caps" or "Risk 1", not a sentence.</summary>
    private static bool IsLabel(string candidate)
    {
        var words = candidate.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is > 0 and <= 5 && words.All(w =>
            char.IsUpper(w[0]) || char.IsDigit(w[0]) || NameConnectors.Contains(w) || w is "/" or "-");
    }

    /// <summary>
    /// Finds runs of capitalised words ("Priya Shah", "Vice President of Finance"). A single capitalised word
    /// at the start of a sentence is ignored, because there capitalisation says nothing about being a name.
    /// </summary>
    /// <param name="segment">Text between sentence or cell boundaries.</param>
    /// <param name="isNameField">True for cells of an owner/assignee column, where even a single leading word is a name.</param>
    private static IEnumerable<NameFact> FindNamesInSegment(string segment, bool isNameField)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var run = new List<string>();
        int runStart = -1;

        IEnumerable<NameFact> Close()
        {
            while (run.Count > 0 && NameConnectors.Contains(run[^1])) run.RemoveAt(run.Count - 1);

            int capitalised = run.Count(w => !NameConnectors.Contains(w));
            bool atSentenceStart = runStart == 0 && !isNameField;

            // A sentence's first word is capitalised regardless of meaning ("Pending VP approval"), so it is
            // not treated as part of a name there; the rest of the run ("VP") is still checked.
            var candidates = atSentenceStart ? run.Skip(1) : run;
            var significant = candidates.Where(w => !NameConnectors.Contains(w) && !NonFactWords.Contains(w)).ToList();

            if (significant.Count > 0 && (!atSentenceStart || capitalised >= 2))
            {
                yield return new NameFact(string.Join(" ", run), significant);
            }

            run.Clear();
            runStart = -1;
        }

        for (int i = 0; i < tokens.Length; i++)
        {
            var raw = tokens[i];
            var word = raw.Trim(',', '.', ';', ':', '!', '?', '"', '\'', '’', '*', '_');
            bool endsRun = raw.Length > 0 && ",.;:!?".Contains(raw[^1]);

            bool isName = word.Length >= 2 && CapitalisedWord.IsMatch(word) && !MonthWords.Contains(word);
            bool isConnector = run.Count > 0 && NameConnectors.Contains(word);

            if (isName || isConnector)
            {
                if (run.Count == 0) runStart = i;
                run.Add(word);
                if (endsRun)
                {
                    foreach (var fact in Close()) yield return fact;
                }
            }
            else if (run.Count > 0)
            {
                foreach (var fact in Close()) yield return fact;
            }
        }

        if (run.Count > 0)
        {
            foreach (var fact in Close()) yield return fact;
        }
    }

    private static bool TryParseNumber(Match m, bool applyScale, out decimal value)
    {
        var digits = m.Groups["num"].Value.Replace(",", string.Empty);
        var text = m.Groups["dec"].Success ? $"{digits}.{m.Groups["dec"].Value}" : digits;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (applyScale && m.Groups["scale"].Success)
        {
            value *= m.Groups["scale"].Value.ToLowerInvariant() switch
            {
                "k" or "thousand" => 1_000m,
                "m" or "mm" or "million" => 1_000_000m,
                _ => 1_000_000_000m // "b", "bn", "billion"
            };
        }

        return true;
    }

    private static decimal ParseDecimal(string text) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static void Mask(StringBuilder text, Match match)
    {
        for (int i = match.Index; i < match.Index + match.Length; i++)
        {
            if (text[i] != '\n') text[i] = ' ';
        }
    }
}
