using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Evaluation;
using FoundrySummarizer.Core.Evaluation.Models;

namespace FoundrySummarizer.Wpf.ViewModels;

public partial class EvaluationViewModel : ObservableObject
{
    private readonly IEvaluationPipeline _pipeline;

    [ObservableProperty]
    private string _documentName = "Current Document";

    [ObservableProperty]
    private string _personaName = "Executive Bullets";

    [ObservableProperty]
    private string _sourceDocumentText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private double _completenessScore = 0.85;

    [ObservableProperty]
    private double _personaAdherenceScore = 0.90;

    [ObservableProperty]
    private double _groundingScore = 1.00;

    [ObservableProperty]
    private bool _contentSafetyPassed = true;

    [ObservableProperty]
    private string _overallStatus = "READY_FOR_EVALUATION";

    [ObservableProperty]
    private bool _isEvaluating;

    [ObservableProperty]
    private string _statusMessage = "Ready to evaluate summary rigor with Microsoft.Extensions.AI.Evaluation.";

    /// <summary>Persona prompt and policies the summary was generated with; facts found here count as grounded.</summary>
    public string ReferenceText { get; private set; } = string.Empty;

    public ObservableCollection<MetricScore> Metrics { get; } = new();
    public ObservableCollection<string> GuardrailWarnings { get; } = new();
    public ObservableCollection<string> Diagnostics { get; } = new();

    public EvaluationViewModel(IEvaluationPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public void UpdateContext(string docName, string persona, string sourceText, string summary, string referenceText = "")
    {
        ReferenceText = referenceText;
        DocumentName = docName;
        PersonaName = persona;
        SourceDocumentText = sourceText;
        SummaryText = summary;
    }

    [RelayCommand]
    public async Task RunEvaluationAsync()
    {
        if (string.IsNullOrWhiteSpace(SummaryText))
        {
            StatusMessage = "Please generate a summary first before running the evaluation!";
            return;
        }

        IsEvaluating = true;
        StatusMessage = "Running Microsoft.Extensions.AI.Evaluation pipeline...";

        try
        {
            var report = await _pipeline.EvaluateSummaryAsync(
                documentName: DocumentName,
                personaName: PersonaName,
                sourceDocumentText: SourceDocumentText,
                summaryText: SummaryText,
                referenceText: ReferenceText
            );

            CompletenessScore = report.CompletenessScore;
            PersonaAdherenceScore = report.PersonaAdherenceScore;
            GroundingScore = report.GroundingScore;
            ContentSafetyPassed = report.ContentSafetyPassed;
            OverallStatus = report.OverallStatus;

            Metrics.Clear();
            foreach (var m in report.Metrics)
            {
                Metrics.Add(m);
            }

            GuardrailWarnings.Clear();
            foreach (var w in report.GuardrailWarnings)
            {
                GuardrailWarnings.Add(w);
            }

            Diagnostics.Clear();
            foreach (var d in report.DiagnosticNotes)
            {
                Diagnostics.Add(d);
            }

            StatusMessage = $"Evaluation complete: Status is {OverallStatus}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Evaluation Error: {ex.Message}";
        }
        finally
        {
            IsEvaluating = false;
        }
    }

    [RelayCommand]
    public async Task TestUnsafeGuardrailAsync()
    {
        SummaryText += "\n\nCONFIDENTIAL TEST LEAK: Customer SSN is 456-78-1234. Secret Master Token: sk-test1234567890abcdef1234567890.";
        StatusMessage = "Injected sensitive PII & token test. Running evaluation to verify guardrail interception...";
        await RunEvaluationAsync();
    }
}
