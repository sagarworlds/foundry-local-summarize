using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace FoundrySummarizer.Core.Evaluation.Evaluators;

public class PersonaAdherenceEvaluator : IEvaluator
{
    public const string MetricName = "PersonaAdherence";

    public IReadOnlyCollection<string> EvaluationMetricNames => new[] { MetricName };

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var systemMsg = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
        var summaryText = response.Text ?? string.Empty;

        double score = CalculateAdherence(systemMsg, summaryText);
        var metric = new NumericMetric(MetricName, score)
        {
            Interpretation = new EvaluationMetricInterpretation(
                score >= 0.80 ? EvaluationRating.Good :
                score >= 0.60 ? EvaluationRating.Average : EvaluationRating.Poor,
                failed: score < 0.60)
        };

        var result = new EvaluationResult();
        result.Metrics[MetricName] = metric;
        return ValueTask.FromResult(result);
    }

    private double CalculateAdherence(string systemInstruction, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return 0.0;

        double score = 0.5; // Baseline

        if (systemInstruction.Contains("Chief of Staff", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("Executive", StringComparison.OrdinalIgnoreCase))
        {
            if (summary.Contains("Financial", StringComparison.OrdinalIgnoreCase) || summary.Contains("$")) score += 0.2;
            if (summary.Contains("Strategic", StringComparison.OrdinalIgnoreCase) || summary.Contains("Milestones", StringComparison.OrdinalIgnoreCase)) score += 0.2;
            if (summary.Contains("Risk", StringComparison.OrdinalIgnoreCase) || summary.Contains("Recommendation", StringComparison.OrdinalIgnoreCase)) score += 0.1;
        }
        else if (systemInstruction.Contains("Action-Item", StringComparison.OrdinalIgnoreCase) ||
                 systemInstruction.Contains("Project Management", StringComparison.OrdinalIgnoreCase))
        {
            if (summary.Contains("|") && summary.Contains("Task")) score += 0.25;
            if (summary.Contains("Assignee") || summary.Contains("Owner")) score += 0.15;
            if (summary.Contains("Deadline") || summary.Contains("Priority")) score += 0.1;
        }
        else if (systemInstruction.Contains("Legal", StringComparison.OrdinalIgnoreCase) ||
                 systemInstruction.Contains("Compliance", StringComparison.OrdinalIgnoreCase))
        {
            if (summary.Contains("Liability", StringComparison.OrdinalIgnoreCase) || summary.Contains("Cap", StringComparison.OrdinalIgnoreCase)) score += 0.2;
            if (summary.Contains("Indemnification", StringComparison.OrdinalIgnoreCase) || summary.Contains("Breach", StringComparison.OrdinalIgnoreCase)) score += 0.2;
            if (summary.Contains("Recommendation", StringComparison.OrdinalIgnoreCase) || summary.Contains("Flag", StringComparison.OrdinalIgnoreCase)) score += 0.1;
        }
        else
        {
            score = 0.85;
        }

        return Math.Min(1.0, Math.Round(score, 2));
    }
}
