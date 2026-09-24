using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using FoundrySummarizer.Core.Evaluation.FactChecking;

namespace FoundrySummarizer.Core.Evaluation.Evaluators;

/// <summary>
/// Anti-hallucination metric: the share of the summary's checkable claims (amounts, percentages, multiples,
/// periods, dates and names) that the source document states. The source is the first user message; an
/// optional <see cref="ReferenceMaterialContext"/> adds material the model may also quote.
/// Each unsupported claim is reported as a warning diagnostic so reviewers can see exactly what to check.
/// </summary>
public class GroundingEvaluator : IEvaluator
{
    public const string MetricName = "Grounding";

    /// <summary>Below this share of supported claims the metric fails.</summary>
    public const double PassThreshold = 0.70;

    /// <summary>At or above this share the metric is rated Good.</summary>
    public const double GoodThreshold = 0.90;

    // Enough to act on without flooding the diagnostic list for a badly hallucinated summary.
    private const int MaxListedClaims = 15;

    public IReadOnlyCollection<string> EvaluationMetricNames => new[] { MetricName };

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var sourceText = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var referenceText = additionalContext?.OfType<ReferenceMaterialContext>().FirstOrDefault()?.Text;
        var summaryText = response.Text ?? string.Empty;

        var metric = string.IsNullOrWhiteSpace(summaryText)
            ? CreateMetric(0.0, "The summary is empty.")
            : Evaluate(summaryText, sourceText, referenceText);

        var result = new EvaluationResult();
        result.Metrics[MetricName] = metric;
        return ValueTask.FromResult(result);
    }

    private static NumericMetric Evaluate(string summaryText, string sourceText, string? referenceText)
    {
        var report = FactChecker.Check(summaryText, sourceText, referenceText);
        var metric = CreateMetric(report.Score, report.Describe());

        var diagnostics = new List<EvaluationDiagnostic>();
        if (report.IsOfflineDemoOutput)
        {
            diagnostics.Add(EvaluationDiagnostic.Error("Grounding: the summary is canned offline demo output, not generated from the document. Start Foundry Local and regenerate."));
        }

        var unsupported = report.Unsupported;
        diagnostics.AddRange(unsupported.Take(MaxListedClaims).Select(f =>
            EvaluationDiagnostic.Warning($"Unverified {f.Fact.Kind.ToString().ToLowerInvariant()} \"{f.Fact.Text}\": {f.MissingDetail}.")));

        if (unsupported.Count > MaxListedClaims)
        {
            diagnostics.Add(EvaluationDiagnostic.Warning($"…and {unsupported.Count - MaxListedClaims} more unverified claims."));
        }

        if (diagnostics.Count > 0)
        {
            metric.Diagnostics = diagnostics;
        }

        return metric;
    }

    private static NumericMetric CreateMetric(double score, string reason) => new(MetricName, score, reason)
    {
        Interpretation = new EvaluationMetricInterpretation(
            score >= GoodThreshold ? EvaluationRating.Good :
            score >= PassThreshold ? EvaluationRating.Average : EvaluationRating.Poor,
            failed: score < PassThreshold,
            reason: reason)
    };
}
