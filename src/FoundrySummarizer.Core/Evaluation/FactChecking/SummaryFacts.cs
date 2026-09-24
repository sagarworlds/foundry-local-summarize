using System.Globalization;

namespace FoundrySummarizer.Core.Evaluation.FactChecking;

/// <summary>Category of a checkable claim.</summary>
public enum FactKind
{
    Amount,
    Percentage,
    Multiplier,
    Duration,
    Date,
    Name
}

/// <summary>
/// A specific, checkable claim found in a summary (a figure, date or proper name).
/// Each kind knows how to look itself up in a <see cref="SourceFactIndex"/>, so new kinds can be added
/// without changing the checker.
/// </summary>
public abstract class SummaryFact
{
    protected SummaryFact(FactKind kind, string text)
    {
        Kind = kind;
        Text = text.Trim();
    }

    /// <summary>The kind of claim.</summary>
    public FactKind Kind { get; }

    /// <summary>The claim as written in the summary.</summary>
    public string Text { get; }

    /// <summary>Normalised identity used to count repeated mentions of the same claim once.</summary>
    public abstract string Key { get; }

    /// <summary>
    /// Checks the claim against the source.
    /// </summary>
    /// <param name="source">Index of what the document and reference material state.</param>
    /// <param name="missingDetail">When unsupported, a short explanation of what could not be found.</param>
    /// <returns>True when the source states the claim.</returns>
    public abstract bool IsSupportedBy(SourceFactIndex source, out string? missingDetail);

    public override string ToString() => $"{Kind}: {Text}";
}

/// <summary>A currency amount such as "$150,000" or "$1.2M", compared by numeric value.</summary>
public sealed class AmountFact : SummaryFact
{
    public AmountFact(string text, decimal value) : base(FactKind.Amount, text) => Value = value;

    public decimal Value { get; }

    public override string Key => "amount:" + Value.ToString(CultureInfo.InvariantCulture);

    public override bool IsSupportedBy(SourceFactIndex source, out string? missingDetail)
    {
        // Compared against every number in the source, so "$150,000" is supported by "150,000 dollars" or "$150k".
        bool supported = source.Numbers.Contains(Value);
        missingDetail = supported ? null : "amount not found in the source";
        return supported;
    }
}

/// <summary>A percentage such as "15%" or "15 percent".</summary>
public sealed class PercentageFact : SummaryFact
{
    public PercentageFact(string text, decimal value) : base(FactKind.Percentage, text) => Value = value;

    public decimal Value { get; }

    public override string Key => "percent:" + Value.ToString(CultureInfo.InvariantCulture);

    public override bool IsSupportedBy(SourceFactIndex source, out string? missingDetail)
    {
        bool supported = source.Percentages.Contains(Value);
        missingDetail = supported ? null : "percentage not found in the source";
        return supported;
    }
}

/// <summary>A multiple such as "2x" (common in liability-cap clauses).</summary>
public sealed class MultiplierFact : SummaryFact
{
    public MultiplierFact(string text, decimal value) : base(FactKind.Multiplier, text) => Value = value;

    public decimal Value { get; }

    public override string Key => "multiplier:" + Value.ToString(CultureInfo.InvariantCulture);

    public override bool IsSupportedBy(SourceFactIndex source, out string? missingDetail)
    {
        bool supported = source.Multipliers.Contains(Value);
        missingDetail = supported ? null : "multiple not found in the source";
        return supported;
    }
}

/// <summary>A period such as "30 days" or "15 calendar days".</summary>
public sealed class DurationFact : SummaryFact
{
    public DurationFact(string text, int count, string unit) : base(FactKind.Duration, text)
    {
        Count = count;
        Unit = unit;
    }

    public int Count { get; }

    /// <summary>Singular lower-case unit, e.g. "day".</summary>
    public string Unit { get; }

    public override string Key => $"duration:{Count} {Unit}";

    public override bool IsSupportedBy(SourceFactIndex source, out string? missingDetail)
    {
        bool supported = source.Durations.Contains(Key);
        missingDetail = supported ? null : "period not found in the source";
        return supported;
    }
}

/// <summary>Date components; null parts were not stated.</summary>
public readonly record struct DateParts(int? Year, int? Month, int? Day, int? Quarter)
{
    /// <summary>
    /// True when every component stated here is also stated, with the same value, in <paramref name="source"/>.
    /// A summary date may be less specific than the source ("March 15" vs "March 15, 2026") but not more.
    /// </summary>
    public bool IsCoveredBy(DateParts source) =>
        Covered(Year, source.Year) && Covered(Month, source.Month) && Covered(Day, source.Day) && Covered(Quarter, source.Quarter);

    private static bool Covered(int? claimed, int? stated) => claimed is null || claimed == stated;
}

/// <summary>
/// A calendar date or quarter. Numeric dates such as "04/05/2026" are ambiguous (April 5 or 4 May),
/// so every valid reading is kept and the fact is supported if any reading matches the source.
/// </summary>
public sealed class DateFact : SummaryFact
{
    public DateFact(string text, IReadOnlyList<DateParts> readings) : base(FactKind.Date, text) => Readings = readings;

    public IReadOnlyList<DateParts> Readings { get; }

    public override string Key => "date:" + string.Join("|", Readings.Select(r => $"{r.Year}-{r.Month}-{r.Day}-Q{r.Quarter}"));

    public override bool IsSupportedBy(SourceFactIndex source, out string? missingDetail)
    {
        bool supported = Readings.Any(r => source.Dates.Any(r.IsCoveredBy));
        missingDetail = supported ? null : "date not found in the source";
        return supported;
    }
}

/// <summary>A proper name or capitalised term such as "Priya Shah" or "Vice President of Finance".</summary>
public sealed class NameFact : SummaryFact
{
    public NameFact(string text, IReadOnlyList<string> significantWords) : base(FactKind.Name, text) => SignificantWords = significantWords;

    /// <summary>The words that must appear in the source (connectors like "of" and "and" are excluded).</summary>
    public IReadOnlyList<string> SignificantWords { get; }

    public override string Key => "name:" + string.Join(" ", SignificantWords).ToLowerInvariant();

    public override bool IsSupportedBy(SourceFactIndex source, out string? missingDetail)
    {
        // Every word must be in the source, which catches a real first name paired with an invented surname.
        var missing = SignificantWords.Where(w => !source.HasWord(w)).ToList();
        missingDetail = missing.Count == 0 ? null : $"not in the source: {string.Join(", ", missing.Select(m => $"'{m}'"))}";
        return missing.Count == 0;
    }
}
