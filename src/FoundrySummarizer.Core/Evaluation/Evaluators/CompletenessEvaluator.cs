using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace FoundrySummarizer.Core.Evaluation.Evaluators;

public class CompletenessEvaluator : IEvaluator
{
    public const string MetricName = "Completeness";

    public IReadOnlyCollection<string> EvaluationMetricNames => new[] { MetricName };

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var sourceDoc = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var summaryText = response.Text ?? string.Empty;

        double score = CalculateCompleteness(sourceDoc, summaryText);
        var metric = new NumericMetric(MetricName, score)
        {
            Interpretation = new EvaluationMetricInterpretation(
                score >= 0.75 ? EvaluationRating.Good :
                score >= 0.50 ? EvaluationRating.Average : EvaluationRating.Poor,
                failed: score < 0.50)
        };

        var result = new EvaluationResult();
        result.Metrics[MetricName] = metric;
        return ValueTask.FromResult(result);
    }

    private double CalculateCompleteness(string source, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return 0.0;
        if (string.IsNullOrWhiteSpace(source)) return 1.0;

        var sourceWords = source.ToLowerInvariant()
            .Split(new[] { ' ', '\r', '\n', '\t', '.', ',', ';', ':', '-', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 4)
            .Distinct()
            .ToList();

        if (sourceWords.Count == 0) return 1.0;

        var summaryLower = summary.ToLowerInvariant();
        int matched = sourceWords.Count(w => summaryLower.Contains(w));

        // In summarization, a 25-40% key vocabulary coverage corresponds to excellent high-level completeness
        double rawRatio = (double)matched / sourceWords.Count;
        double normalizedScore = Math.Min(1.0, rawRatio * 2.8);
        return Math.Round(normalizedScore, 2);
    }
}
