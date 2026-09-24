using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Grounding;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;

namespace FoundrySummarizer.Wpf.ViewModels;

public partial class SummarizerViewModel : ObservableObject
{
    private readonly IPromptyEngine _promptyEngine;
    private readonly HybridChatClientRouter _router;
    private readonly IVectorGroundingService _groundingService;
    private readonly IDocumentSummarizer _summarizer;

    [ObservableProperty]
    private PromptyDocument? _selectedPersona;

    [ObservableProperty]
    private string _systemPrompt = string.Empty;

    [ObservableProperty]
    private string _userPromptTemplate = string.Empty;

    [ObservableProperty]
    private bool _isGroundingEnabled = true;

    [ObservableProperty]
    private string _generatedSummary = string.Empty;

    [ObservableProperty]
    private RoutingDecisionInfo? _lastRoutingInfo;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string _generationStatus = "Ready to generate summary.";

    public ObservableCollection<PromptyDocument> AvailablePersonas { get; } = new();

    public string CurrentDocumentText { get; set; } = string.Empty;
    public string CurrentDocumentName { get; set; } = string.Empty;

    public event Action<string, string>? OnSummaryGenerated;

    public SummarizerViewModel(IPromptyEngine promptyEngine, HybridChatClientRouter router, IVectorGroundingService groundingService, IDocumentSummarizer summarizer)
    {
        _summarizer = summarizer;
        _promptyEngine = promptyEngine;
        _router = router;
        _groundingService = groundingService;

        _router.OnRoutingDecision += decision =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                LastRoutingInfo = decision;
            });
        };

        LoadPersonas();
    }

    private void LoadPersonas()
    {
        AvailablePersonas.Clear();
        foreach (var p in _promptyEngine.AvailablePersonas)
        {
            AvailablePersonas.Add(p);
        }

        SelectedPersona = AvailablePersonas.FirstOrDefault();
    }

    partial void OnSelectedPersonaChanged(PromptyDocument? value)
    {
        if (value != null)
        {
            SystemPrompt = value.SystemPrompt;
            UserPromptTemplate = value.UserPromptTemplate;
        }
    }

    [RelayCommand]
    public async Task GenerateSummaryAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentDocumentText))
        {
            GenerationStatus = "Please ingest or load a document first!";
            return;
        }

        if (SelectedPersona == null)
        {
            GenerationStatus = "Please select a summary persona.";
            return;
        }

        IsGenerating = true;
        GenerationStatus = $"Running summarization pipeline ({SelectedPersona.Name})...";

        try
        {
            string groundingContext = string.Empty;
            if (IsGroundingEnabled)
            {
                GenerationStatus = "Querying Microsoft.Extensions.VectorData for policy cross-references...";
                groundingContext = await _groundingService.BuildGroundingPromptContextAsync(CurrentDocumentText);
            }

            var activeDoc = SelectedPersona with
            {
                SystemPrompt = SystemPrompt,
                UserPromptTemplate = UserPromptTemplate
            };

            // Long documents are read in parts by the local model; skip that when the router will send
            // the request to the large-context cloud model, which can read the whole document at once.
            bool allowMultiPart = !_router.WouldEscalateToCloud(SemanticChunker.EstimateTokens(CurrentDocumentText));
            var request = new SummarizationRequest(activeDoc, CurrentDocumentText, groundingContext, allowMultiPart);

            // Progress<T> captures the UI synchronization context, so status updates are marshalled safely.
            var progress = new Progress<string>(status => GenerationStatus = status);
            var result = await _summarizer.SummarizeAsync(request, progress);

            GeneratedSummary = result.Summary;
            var route = LastRoutingInfo?.RouteName ?? "Foundry Local";
            GenerationStatus = result.UsedMultiPart
                ? $"Summary generated from {result.PartCount} document parts via {route}"
                : $"Summary generated successfully via {route}";
            OnSummaryGenerated?.Invoke(CurrentDocumentName, GeneratedSummary);
        }
        catch (Exception ex)
        {
            GenerationStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    public void CopySummary()
    {
        if (!string.IsNullOrWhiteSpace(GeneratedSummary))
        {
            Clipboard.SetText(GeneratedSummary);
            GenerationStatus = "Summary copied to clipboard!";
        }
    }

    [RelayCommand]
    public void ResetTemplate()
    {
        if (SelectedPersona != null)
        {
            SystemPrompt = SelectedPersona.SystemPrompt;
            UserPromptTemplate = SelectedPersona.UserPromptTemplate;
            GenerationStatus = "Prompt templates reset to defaults.";
        }
    }
}
