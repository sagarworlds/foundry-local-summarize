using System.Globalization;
using System.Text.RegularExpressions;

namespace FoundrySummarizer.Core.Verification;

/// <summary>What kind of figure was found.</summary>
public enum FigureKind
{
    /// <summary>A money amount, e.g. "$150,000" or "€1.2m".</summary>
    Amount,

    /// <summary>A percentage, e.g. "12.5%".</summary>
    Percentage,

    /// <summary>A calendar date, e.g. "30 June 2026" or "2026-06-30".</summary>
    Date,

    /// <summary>A period or multiple, e.g. "30 days", "17.5 months" or "3x".</summary>
    Quantity,

    /// <summary>Another significant number, e.g. "1,200 units" or "4.2".</summary>
    Number
}

/// <summary>A figure found in generated text.</summary>
/// <param name="Text">The figure as written, e.g. "$150,000".</param>
/// <param name="Kind">What kind of figure it is.</param>
public record Figure(string Text, FigureKind Kind);

/// <summary>Outcome of checking generated text against its source document.</summary>
/// <param name="Checked">Every figure found in the generated text, in order of appearance.</param>
/// <param name="NotFound">The figures that do not appear in the document in any form.</param>
public record FigureCheckResult(IReadOnlyList<Figure> Checked, IReadOnlyList<Figure> NotFound)
{
    /// <summary>A result for text that contains no figures.</summary>
    public static FigureCheckResult Empty { get; } = new(Array.Empty<Figure>(), Array.Empty<Figure>());

    /// <summary>True when every figure appears in the document.</summary>
    public bool AllFound => NotFound.Count == 0;
}

/// <summary>Checks that the figures in model output come from the source document.</summary>
public interface IFigureChecker
{
    /// <summary>
    /// Finds the amounts, percentages, dates and significant numbers in <paramref name="generatedText"/> and reports
    /// those that do not appear in <paramref name="sourceText"/>.
    /// </summary>
    /// <param name="generatedText">A summary or answer written by the model.</param>
    /// <param name="sourceText">The document it was written from.</param>
    FigureCheckResult Check(string generatedText, string sourceText);
}

/// <summary>
/// Catches invented or altered figures, the most harmful mistake a summarizer makes, without another model call.
/// A figure counts as found when the document contains the same value in any common form: "$1.5 million" matches
/// "$1,500,000", "15 percent" matches "15%", and "June 30, 2026" matches "30/06/2026". A figure the model computed
/// itself (a total, a difference) is reported too: it is not in the document, so the reader should check it.
/// </summary>
public sealed class FigureChecker : IFigureChecker
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // A number with optional thousands separators (1,500,000 or Indian 15,00,000) and decimals; never ending in a
    // comma, so "in 2026, the…" yields 2026.
    private const string NumberPattern = @"\d(?:[\d,]*\d)?(?:\.\d+)?";

    // Words and suffixes that multiply a number; "m" and "k" only directly after the digits ("$1.5m", "150k").
    private const string ScalePattern = @"(?:\s?(?<scale>thousand|million|billion|trillion|lakhs?|crores?|bn)\b|(?<scale>[km])\b)?";

    private const string CurrencySymbol = @"[$€£¥₹]|US\$|A\$|C\$";
    private const string CurrencyCode = @"USD|EUR|GBP|INR|JPY|AUD|CAD|CHF|CNY|Rs\.?";

    private static readonly Regex Amount = new(
        $@"(?:(?<![\w$])(?:{CurrencySymbol}|(?:{CurrencyCode})\s?)\s?(?<number>{NumberPattern}){ScalePattern})" +
        $@"|(?:(?<![\w.,])(?<number>{NumberPattern}){ScalePattern}\s?(?:{CurrencyCode})\b)", Options);

    private static readonly Regex Percentage = new($@"(?<![\w.,])(?<number>{NumberPattern})\s?(?:%|percent\b|per\s?cent\b)", Options);

    private static readonly string[] MonthNames =
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

    private const string MonthPattern = @"(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?";

    private static readonly Regex[] DatePatterns =
    {
        new(@"(?<!\d)(?<year>\d{4})-(?<mm>\d{1,2})-(?<day>\d{1,2})(?!\d)", Options),                                   // 2026-06-30
        new($@"(?<!\d)(?<day>\d{{1,2}})(?:st|nd|rd|th)?\s(?:of\s)?{MonthPattern},?(?:\s(?<year>\d{{4}}))?(?!\d)", Options),  // 30 June 2026
        new($@"\b{MonthPattern}\s(?<day>\d{{1,2}})(?:st|nd|rd|th)?(?:,?\s(?<year>\d{{4}}))?(?!\d)", Options),             // June 30, 2026
        new($@"\b{MonthPattern}\s(?<year>\d{{4}})(?!\d)", Options),                                                        // June 2026
        new(@"(?<![\d/.])(?<a>\d{1,2})[/.](?<b>\d{1,2})[/.](?<year>\d{4}|\d{2})(?![\d/]|\.\d)", Options),              // 30/06/2026, 06/30/26 (a full stop may follow)
    };

    // Periods and multiples are checked whatever their size: a "15 days" notice period or a "3x" liability cap is as
    // important as any amount, and small numbers are exactly where a model slips ("30 days" for "15 days").
    private static readonly Regex Quantity = new(
        @"(?<![\w.,$€£¥₹/-])(?<number>\d+(?:\.\d+)?)(?:\s?[x×](?!\w)|\s?(?:business\s|working\s|calendar\s)?(?:days?|weeks?|months?|years?|hours?|minutes?)\b)", Options);

    // Numbers worth checking on their own: three or more digits, or a decimal ("1,200", "4.2"). Smaller whole
    // numbers are mostly counts and list markers ("3 risks", "### 1."), too noisy to report.
    private static readonly Regex SignificantNumber = new($@"(?<![\w.,$€£¥₹/-])(?<number>{NumberPattern})(?![\w/]|[.,]\d)", Options);

    /// <inheritdoc />
    public FigureCheckResult Check(string generatedText, string sourceText)
    {
        if (string.IsNullOrWhiteSpace(generatedText)) return FigureCheckResult.Empty;

        var figures = Extract(generatedText);
        if (figures.Count == 0) return FigureCheckResult.Empty;

        var source = SourceIndex.Build(sourceText ?? string.Empty);
        var notFound = figures.Where(f => !source.Contains(f)).Select(f => f.Figure).ToList();
        return new FigureCheckResult(figures.Select(f => f.Figure).ToList(), notFound);
    }

    /// <summary>A figure with the values it can match.</summary>
    private sealed record Found(Figure Figure, IReadOnlyList<decimal> Values, IReadOnlyList<DateKey> Dates);

    /// <summary>A date for matching; a missing part (null) matches any value.</summary>
    private readonly record struct DateKey(int? Year, int Month, int? Day)
    {
        public bool Matches(DateKey other) =>
            Month == other.Month
            && (Day is null || other.Day is null || Day == other.Day)
            && (Year is null || other.Year is null || Year == other.Year);
    }

    private static List<Found> Extract(string text)
    {
        var found = new List<(int Index, Found Found)>();
        var taken = new List<(int Start, int End)>();

        bool Overlaps(Match m) => taken.Any(t => m.Index < t.End && t.Start < m.Index + m.Length);
        void Take(Match m, Found f)
        {
            found.Add((m.Index, f));
            taken.Add((m.Index, m.Index + m.Length));
        }

        // Most specific first: a date or amount must not also be reported as its bare numbers.
        foreach (var (m, dates) in ExtractDates(text))
        {
            Take(m, new Found(new Figure(m.Value.Trim(), FigureKind.Date), Array.Empty<decimal>(), dates));
        }

        foreach (Match m in Amount.Matches(text))
        {
            if (Overlaps(m) || ParseValue(m) is not { } value) continue;
            Take(m, new Found(new Figure(m.Value.Trim(), FigureKind.Amount), new[] { value }, Array.Empty<DateKey>()));
        }

        foreach (Match m in Percentage.Matches(text))
        {
            if (Overlaps(m) || ParseValue(m) is not { } value) continue;
            Take(m, new Found(new Figure(m.Value.Trim(), FigureKind.Percentage), new[] { value }, Array.Empty<DateKey>()));
        }

        foreach (Match m in Quantity.Matches(text))
        {
            if (Overlaps(m) || ParseValue(m) is not { } value) continue;
            Take(m, new Found(new Figure(m.Value.Trim(), FigureKind.Quantity), new[] { value }, Array.Empty<DateKey>()));
        }

        foreach (Match m in SignificantNumber.Matches(text))
        {
            if (Overlaps(m) || IsCitation(text, m) || ParseValue(m) is not { } value) continue;
            var digits = m.Groups["number"].Value.Count(char.IsDigit);
            if (digits < 3 && !m.Groups["number"].Value.Contains('.')) continue;
            Take(m, new Found(new Figure(m.Value.Trim(), FigureKind.Number), new[] { value }, Array.Empty<DateKey>()));
        }

        return found.OrderBy(f => f.Index).Select(f => f.Found)
            .DistinctBy(f => (f.Figure.Text.ToLowerInvariant(), f.Figure.Kind))
            .ToList();
    }

    /// <summary>
    /// The dates in <paramref name="text"/>, most specific pattern first and without overlaps, so the "June 2026"
    /// inside "30 June 2026" is not read as a second, day-less date (which would match any day in June).
    /// </summary>
    private static List<(Match Match, IReadOnlyList<DateKey> Dates)> ExtractDates(string text)
    {
        var found = new List<(Match, IReadOnlyList<DateKey>)>();
        foreach (var pattern in DatePatterns)
        {
            foreach (Match m in pattern.Matches(text))
            {
                if (found.Any(f => m.Index < f.Item1.Index + f.Item1.Length && f.Item1.Index < m.Index + m.Length)) continue;
                var dates = ParseDates(m);
                if (dates.Count > 0) found.Add((m, dates));
            }
        }

        return found;
    }

    // "[P12]" is a passage citation written by the chat, not a figure.
    private static bool IsCitation(string text, Match m) => m.Index > 0 && text[m.Index - 1] is 'P' or 'p' && m.Index > 1 && text[m.Index - 2] == '[';

    /// <summary>The number in <paramref name="m"/> times its scale word, or null when it is not a number.</summary>
    private static decimal? ParseValue(Match m)
    {
        var digits = m.Groups["number"].Value.Replace(",", string.Empty).TrimEnd('.');
        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;

        var scale = m.Groups["scale"].Success ? m.Groups["scale"].Value.ToLowerInvariant() : string.Empty;
        return scale switch
        {
            "k" or "thousand" => value * 1_000m,
            "m" or "million" => value * 1_000_000m,
            "bn" or "billion" => value * 1_000_000_000m,
            "trillion" => value * 1_000_000_000_000m,
            "lakh" or "lakhs" => value * 100_000m,
            "crore" or "crores" => value * 10_000_000m,
            _ => value
        };
    }

    private static IReadOnlyList<DateKey> ParseDates(Match m)
    {
        int? year = m.Groups["year"].Success ? NormalizeYear(int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture)) : null;
        int? day = m.Groups["day"].Success ? int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture) : null;

        if (m.Groups["month"].Success)
        {
            var month = MonthNumber(m.Groups["month"].Value);
            return Valid(month, day) ? new[] { new DateKey(year, month, day) } : Array.Empty<DateKey>();
        }

        if (m.Groups["mm"].Success)
        {
            var mo = int.Parse(m.Groups["mm"].Value, CultureInfo.InvariantCulture);
            return Valid(mo, day) ? new[] { new DateKey(year, mo, day) } : Array.Empty<DateKey>();
        }

        // 06/07/2026 is 6 July in most of the world and June 7 in the US: either reading may match the document.
        var a = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
        var b = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
        var readings = new List<DateKey>();
        if (Valid(b, a)) readings.Add(new DateKey(year, b, a));
        if (Valid(a, b) && a != b) readings.Add(new DateKey(year, a, b));
        return readings;
    }

    private static int NormalizeYear(int year) => year < 100 ? 2000 + year : year;

    private static bool Valid(int month, int? day) => month is >= 1 and <= 12 && (day is null || day is >= 1 and <= 31);

    /// <summary>1–12 for a month name or abbreviation matched by the month pattern ("Jun", "Sept.", "June").</summary>
    private static int MonthNumber(string name)
    {
        var lower = name.TrimEnd('.').ToLowerInvariant();
        return Array.FindIndex(MonthNames, month => month.StartsWith(lower, StringComparison.Ordinal)) + 1;
    }

    /// <summary>Every value and date in the source document, in normalized form.</summary>
    private sealed class SourceIndex
    {
        private readonly HashSet<decimal> _values = new();
        private readonly HashSet<decimal> _percentages = new();
        private readonly List<DateKey> _dates = new();

        public static SourceIndex Build(string text)
        {
            var index = new SourceIndex();
            foreach (var (_, dates) in ExtractDates(text)) index._dates.AddRange(dates);

            foreach (Match m in Percentage.Matches(text))
            {
                if (ParseValue(m) is { } value) index._percentages.Add(value);
            }

            // Every number counts, with and without its scale word: "1.5 million" provides both 1,500,000 and 1.5.
            foreach (Match m in Amount.Matches(text))
            {
                if (ParseValue(m) is { } value) index._values.Add(value);
            }

            foreach (Match m in AnyNumber.Matches(text))
            {
                if (ParseValue(m) is { } value) index._values.Add(value);
                if (decimal.TryParse(m.Groups["number"].Value.Replace(",", string.Empty).TrimEnd('.'), NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var bare))
                {
                    index._values.Add(bare);
                }
            }

            return index;
        }

        public bool Contains(Found found) => found.Figure.Kind switch
        {
            FigureKind.Date => found.Dates.Any(d => _dates.Any(s => s.Matches(d))),
            FigureKind.Percentage => found.Values.Any(_percentages.Contains),
            _ => found.Values.Any(_values.Contains)
        };
    }

    private static readonly Regex AnyNumber = new($@"(?<number>{NumberPattern}){ScalePattern}", Options);
}
