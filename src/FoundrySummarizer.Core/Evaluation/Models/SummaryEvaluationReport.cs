namespace FoundrySummarizer.Core.Evaluation.Models;

public record MetricScore(
    string Name,
    double Value,
    string Interpretation,
    bool Passed,
    string? Details = null
);

public record SummaryEvaluationReport(
    string DocumentName,
    string PersonaName,
    DateTime EvaluatedAt,
    double CompletenessScore,
    double PersonaAdherenceScore,
    double GroundingScore,
    bool ContentSafetyPassed,
    string OverallStatus,
    IReadOnlyList<MetricScore> Metrics,
    IReadOnlyList<string> GuardrailWarnings,
    IReadOnlyList<string> DiagnosticNotes
);
