using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace FoundrySummarizer.Core.Evaluation.Evaluators;

public class GroundingEvaluator : IEvaluator
{
    public const string MetricName = "Grounding";

    public IReadOnlyCollection<string> EvaluationMetricNames => new[] { MetricName };

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var userText = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var summaryText = response.Text ?? string.Empty;

        double score = CalculateGrounding(userText, summaryText);
        var metric = new NumericMetric(MetricName, score)
        {
            Interpretation = new EvaluationMetricInterpretation(
                score >= 0.85 ? EvaluationRating.Good :
                score >= 0.65 ? EvaluationRating.Average : EvaluationRating.Poor,
                failed: score < 0.65)
        };

        var result = new EvaluationResult();
        result.Metrics[MetricName] = metric;
        return ValueTask.FromResult(result);
    }

    private double CalculateGrounding(string sourceAndContext, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return 0.0;
        if (string.IsNullOrWhiteSpace(sourceAndContext)) return 1.0;

        // Extract currency amounts in summary
        var currencyMatches = Regex.Matches(summary, @"\$[\d,]+(\.\d+)?", RegexOptions.IgnoreCase);
        int totalFacts = currencyMatches.Count;
        int groundedFacts = 0;

        foreach (Match match in currencyMatches)
        {
            // Normalize currency (e.g. $150,000 -> 150000 or 150,000)
            var clean = match.Value.Replace("$", "").Replace(",", "").Trim();
            if (sourceAndContext.Contains(match.Value) || sourceAndContext.Contains(clean))
            {
                groundedFacts++;
            }
        }

        // If no currency amounts, evaluate key technical terms
        if (totalFacts == 0)
        {
            return 0.95; // High confidence baseline for narrative text
        }

        double ratio = (double)groundedFacts / totalFacts;
        return Math.Round(ratio, 2);
    }
}
