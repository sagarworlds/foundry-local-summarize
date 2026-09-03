using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using FoundrySummarizer.Core.Evaluation.Evaluators;
using FoundrySummarizer.Core.Evaluation.Models;

namespace FoundrySummarizer.Core.Evaluation;

public interface IEvaluationPipeline
{
    Task<SummaryEvaluationReport> EvaluateSummaryAsync(
        string documentName,
        string personaName,
        string sourceDocumentText,
        string summaryText,
        CancellationToken cancellationToken = default);
}

public class EvaluationPipeline : IEvaluationPipeline
{
    private readonly List<IEvaluator> _evaluators;

    public EvaluationPipeline(IEnumerable<IEvaluator>? evaluators = null)
    {
        _evaluators = evaluators?.ToList() ?? new List<IEvaluator>
        {
            new CompletenessEvaluator(),
            new PersonaAdherenceEvaluator(),
            new GroundingEvaluator(),
            new ContentSafetyGuardrailEvaluator()
        };
    }

    public async Task<SummaryEvaluationReport> EvaluateSummaryAsync(
        string documentName,
        string personaName,
        string sourceDocumentText,
        string summaryText,
        CancellationToken cancellationToken = default)
    {
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, $"Persona: {personaName}"),
            new(ChatRole.User, sourceDocumentText)
        };

        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, summaryText));

        double completeness = 0.0;
        double adherence = 0.0;
        double grounding = 0.0;
        bool safetyPassed = true;
        var scoresList = new List<MetricScore>();
        var guardrailWarnings = new List<string>();
        var diagnostics = new List<string>();

        foreach (var evaluator in _evaluators)
        {
            var evalResult = await evaluator.EvaluateAsync(chatMessages, chatResponse, cancellationToken: cancellationToken);

            foreach (var (metricName, metric) in evalResult.Metrics)
            {
                if (metric is NumericMetric numMetric)
                {
                    double val = numMetric.Value ?? 0.0;
                    string rating = numMetric.Interpretation?.Rating.ToString() ?? "Normal";
                    bool passed = !(numMetric.Interpretation?.Failed ?? false);

                    if (metricName == CompletenessEvaluator.MetricName) completeness = val;
                    else if (metricName == PersonaAdherenceEvaluator.MetricName) adherence = val;
                    else if (metricName == GroundingEvaluator.MetricName) grounding = val;

                    scoresList.Add(new MetricScore(metricName, val, rating, passed));
                }
                else if (metric is BooleanMetric boolMetric)
                {
                    bool val = boolMetric.Value ?? false;
                    string rating = val ? "Good" : "Violation";
                    if (metricName == ContentSafetyGuardrailEvaluator.MetricName) safetyPassed = val;

                    scoresList.Add(new MetricScore(metricName, val ? 1.0 : 0.0, rating, val));
                }

                if (metric.Diagnostics != null)
                {
                    foreach (var diag in metric.Diagnostics)
                    {
                        if (diag.Severity == EvaluationDiagnosticSeverity.Error)
                        {
                            guardrailWarnings.Add(diag.Message);
                        }
                        else
                        {
                            diagnostics.Add(diag.Message);
                        }
                    }
                }
            }
        }

        string overallStatus = !safetyPassed
            ? "BLOCKED_BY_GUARDRAIL"
            : (completeness >= 0.70 && adherence >= 0.70 && grounding >= 0.70)
                ? "EXCELLENT"
                : (completeness >= 0.50 && adherence >= 0.50)
                    ? "GOOD"
                    : "NEEDS_REVIEW";

        return new SummaryEvaluationReport(
            DocumentName: documentName,
            PersonaName: personaName,
            EvaluatedAt: DateTime.UtcNow,
            CompletenessScore: completeness,
            PersonaAdherenceScore: adherence,
            GroundingScore: grounding,
            ContentSafetyPassed: safetyPassed,
            OverallStatus: overallStatus,
            Metrics: scoresList,
            GuardrailWarnings: guardrailWarnings,
            DiagnosticNotes: diagnostics
        );
    }
}
