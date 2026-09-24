using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Core.Evaluation.FactChecking;

/// <summary>The outcome of checking one claim.</summary>
/// <param name="Fact">The claim.</param>
/// <param name="Supported">True when the source states it.</param>
/// <param name="MissingDetail">For unsupported claims, what could not be found.</param>
public record CheckedFact(SummaryFact Fact, bool Supported, string? MissingDetail);

/// <summary>Result of fact-checking one summary.</summary>
public sealed class FactCheckReport
{
    public FactCheckReport(IReadOnlyList<CheckedFact> facts, bool isOfflineDemoOutput)
    {
        Facts = facts;
        IsOfflineDemoOutput = isOfflineDemoOutput;
    }

    /// <summary>Every distinct claim found in the summary, in order of appearance.</summary>
    public IReadOnlyList<CheckedFact> Facts { get; }

    /// <summary>True when the summary is canned demo text from the offline fallback engine.</summary>
    public bool IsOfflineDemoOutput { get; }

    public IReadOnlyList<CheckedFact> Unsupported => Facts.Where(f => !f.Supported).ToList();

    /// <summary>
    /// Share of claims the source supports (0–1). A summary with no checkable claims scores 1, since it asserts
    /// nothing false; demo output scores 0 because none of it was derived from the document.
    /// </summary>
    public double Score => IsOfflineDemoOutput ? 0.0
        : Facts.Count == 0 ? 1.0
        : Math.Round((double)Facts.Count(f => f.Supported) / Facts.Count, 2);

    /// <summary>One-line summary such as "Verified 5 of 6 facts (amounts 2/2, names 3/4)."</summary>
    public string Describe()
    {
        if (IsOfflineDemoOutput) return "Summary is offline demo output and was not generated from the document.";
        if (Facts.Count == 0) return "No checkable figures, dates or names found in the summary.";

        var byKind = Facts
            .GroupBy(f => f.Fact.Kind)
            .OrderBy(g => g.Key)
            .Select(g => $"{KindLabel(g.Key)} {g.Count(f => f.Supported)}/{g.Count()}");

        return $"Verified {Facts.Count(f => f.Supported)} of {Facts.Count} facts ({string.Join(", ", byKind)}).";
    }

    private static string KindLabel(FactKind kind) => kind switch
    {
        FactKind.Amount => "amounts",
        FactKind.Percentage => "percentages",
        FactKind.Multiplier => "multiples",
        FactKind.Duration => "periods",
        FactKind.Date => "dates",
        _ => "names"
    };
}

/// <summary>Checks every figure, date and name in a summary against the source it was generated from.</summary>
public static class FactChecker
{
    /// <summary>Fact-checks <paramref name="summary"/>.</summary>
    /// <param name="summary">The generated summary.</param>
    /// <param name="sourceText">The document it summarizes.</param>
    /// <param name="referenceText">Other material the model was given and may quote (persona prompt, policies).</param>
    public static FactCheckReport Check(string summary, string sourceText, string? referenceText = null)
    {
        summary ??= string.Empty;
        if (summary.TrimStart().StartsWith(HybridChatClientRouter.FallbackNotice.Trim(), StringComparison.Ordinal))
        {
            return new FactCheckReport(Array.Empty<CheckedFact>(), isOfflineDemoOutput: true);
        }

        var index = new SourceFactIndex(sourceText ?? string.Empty, referenceText);
        var results = FactExtractor.ExtractFromSummary(summary)
            .Select(fact => new CheckedFact(fact, fact.IsSupportedBy(index, out var missing), missing))
            .ToList();

        return new FactCheckReport(results, isOfflineDemoOutput: false);
    }
}
