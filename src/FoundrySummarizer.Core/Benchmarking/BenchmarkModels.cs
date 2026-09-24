namespace FoundrySummarizer.Core.Benchmarking;

/// <summary>A document to summarize during a benchmark.</summary>
/// <param name="Name">Display name, usually the file name.</param>
/// <param name="Text">Extracted document text.</param>
public record BenchmarkDocument(string Name, string Text);

/// <summary>Outcome of summarizing one document with one persona on one model.</summary>
public record BenchmarkRunResult(
    string ModelId,
    string DocumentName,
    string PersonaName,
    int Run,
    double Grounding,
    double Completeness,
    double PersonaAdherence,
    bool SafetyPassed,
    string Status,
    TimeSpan Duration,
    bool UsedMultiPart,
    IReadOnlyList<string> UnverifiedClaims,
    string Summary,
    string? Error)
{
    /// <summary>False when the model call or evaluation failed; scores are then 0 and <see cref="Error"/> says why.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>Aggregate scores for one model across every document, persona and repeat run.</summary>
public record ModelScorecard(
    string ModelId,
    int Runs,
    int Failures,
    double MeanGrounding,
    double MinGrounding,
    double MeanCompleteness,
    double MeanPersonaAdherence,
    int NeedsReviewCount,
    TimeSpan MeanDuration);

/// <summary>All results of one benchmark session.</summary>
public sealed class BenchmarkReport
{
    public BenchmarkReport(DateTimeOffset startedAt, IReadOnlyList<BenchmarkRunResult> results)
    {
        StartedAt = startedAt;
        Results = results;
    }

    public DateTimeOffset StartedAt { get; }

    public IReadOnlyList<BenchmarkRunResult> Results { get; }

    /// <summary>
    /// One scorecard per model, best first. Means cover successful runs only, so a model that mostly failed is
    /// not rewarded; its <see cref="ModelScorecard.Failures"/> count shows that instead.
    /// </summary>
    public IReadOnlyList<ModelScorecard> Scorecards => Results
        .GroupBy(r => r.ModelId, StringComparer.Ordinal)
        .Select(g =>
        {
            var ok = g.Where(r => r.Succeeded).ToList();
            double Mean(Func<BenchmarkRunResult, double> selector) => ok.Count == 0 ? 0 : Math.Round(ok.Average(selector), 3);

            return new ModelScorecard(
                ModelId: g.Key,
                Runs: g.Count(),
                Failures: g.Count() - ok.Count,
                MeanGrounding: Mean(r => r.Grounding),
                MinGrounding: ok.Count == 0 ? 0 : ok.Min(r => r.Grounding),
                MeanCompleteness: Mean(r => r.Completeness),
                MeanPersonaAdherence: Mean(r => r.PersonaAdherence),
                NeedsReviewCount: ok.Count(r => r.Status != "EXCELLENT" && r.Status != "GOOD"),
                MeanDuration: ok.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ok.Average(r => r.Duration.TotalMilliseconds)));
        })
        .OrderBy(s => s.Failures == s.Runs)            // models with no successful run last
        .ThenByDescending(s => s.MeanGrounding)
        .ThenByDescending(s => s.MeanCompleteness)
        .ToList();
}
