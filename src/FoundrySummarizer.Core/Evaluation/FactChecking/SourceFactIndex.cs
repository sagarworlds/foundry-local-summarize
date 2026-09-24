using System.Text.RegularExpressions;

namespace FoundrySummarizer.Core.Evaluation.FactChecking;

/// <summary>
/// Everything a summary is allowed to state: the figures, dates and words found in the source document
/// plus any reference material the model was given (persona prompt, matched company policies).
/// </summary>
public sealed class SourceFactIndex
{
    private static readonly Regex WordRegex = new(@"[\p{L}][\p{L}'’\-]*", RegexOptions.Compiled);
    private static readonly Regex CapitalisedRun = new(@"\b[A-Z][A-Za-z]*(?:\s+(?:(?:of|and|&|for)\s+)?[A-Z][A-Za-z]*)+", RegexOptions.Compiled);

    private readonly HashSet<string> _words = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acronyms = new(StringComparer.Ordinal);

    /// <param name="sourceText">The document the summary was generated from.</param>
    /// <param name="referenceText">Other material the summary may legitimately quote; may be null.</param>
    public SourceFactIndex(string sourceText, string? referenceText = null)
    {
        var text = $"{sourceText}\n{referenceText}";

        Numbers = FactExtractor.FindNumbers(text).ToHashSet();
        Percentages = FactExtractor.FindPercentages(text).ToHashSet();
        Multipliers = FactExtractor.FindMultipliers(text).ToHashSet();
        Durations = FactExtractor.FindDurationKeys(text).ToHashSet(StringComparer.Ordinal);
        Dates = FactExtractor.FindDateParts(text).ToList();

        foreach (Match m in WordRegex.Matches(text))
        {
            AddWord(m.Value);
        }

        // "Vice President of Finance" → VP, VPF, PF: lets a summary say "VP" when the source spells it out.
        foreach (Match m in CapitalisedRun.Matches(text))
        {
            var initials = m.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsUpper(w[0]))
                .Select(w => w[0])
                .ToArray();

            for (int start = 0; start < initials.Length; start++)
            {
                for (int length = 2; length <= 6 && start + length <= initials.Length; length++)
                {
                    _acronyms.Add(new string(initials, start, length));
                }
            }
        }
    }

    /// <summary>Every number in the source, as written and with scale words (k, million) applied.</summary>
    public IReadOnlySet<decimal> Numbers { get; }

    /// <summary>Percentages stated in the source.</summary>
    public IReadOnlySet<decimal> Percentages { get; }

    /// <summary>Multiples such as 2x stated in the source.</summary>
    public IReadOnlySet<decimal> Multipliers { get; }

    /// <summary>Durations stated in the source, as <see cref="DurationFact.Key"/> strings.</summary>
    public IReadOnlySet<string> Durations { get; }

    /// <summary>All readings of all dates stated in the source.</summary>
    public IReadOnlyList<DateParts> Dates { get; }

    /// <summary>
    /// True when <paramref name="word"/> occurs in the source, ignoring case, possessives and simple plurals,
    /// or when it is an acronym of a capitalised phrase in the source.
    /// </summary>
    public bool HasWord(string word)
    {
        var w = Normalize(word);
        if (w.Length == 0) return true;
        if (_words.Contains(w)) return true;

        // Simple plural/singular variants so "Vendors" is supported by "vendor" and vice versa.
        if (w.EndsWith('s') && _words.Contains(w[..^1])) return true;
        if (_words.Contains(w + "s")) return true;
        if (w.EndsWith("ies", StringComparison.Ordinal) && _words.Contains(w[..^3] + "y")) return true;

        // Hyphenated names ("Jean-Luc") are supported when each part is.
        if (w.Contains('-') && w.Split('-', StringSplitOptions.RemoveEmptyEntries).All(p => _words.Contains(p))) return true;

        bool isAcronym = word.Length is >= 2 and <= 6 && word.All(char.IsUpper);
        return isAcronym && _acronyms.Contains(word);
    }

    private void AddWord(string raw)
    {
        var w = Normalize(raw);
        if (w.Length == 0) return;
        _words.Add(w);
        foreach (var part in w.Split('-', StringSplitOptions.RemoveEmptyEntries)) _words.Add(part);
    }

    private static string Normalize(string word)
    {
        var w = word.ToLowerInvariant().Replace('’', '\'').Trim('\'', '-');
        if (w.EndsWith("'s", StringComparison.Ordinal)) w = w[..^2];
        return w;
    }
}
