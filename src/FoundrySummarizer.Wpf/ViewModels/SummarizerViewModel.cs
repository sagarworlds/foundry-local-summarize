using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Grounding;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Wpf.ViewModels;

public partial class SummarizerViewModel : ObservableObject
{
    private readonly IPromptyEngine _promptyEngine;
    private readonly HybridChatClientRouter _router;
    private readonly IVectorGroundingService _groundingService;

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

    public SummarizerViewModel(IPromptyEngine promptyEngine, HybridChatClientRouter router, IVectorGroundingService groundingService)
    {
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

            var variables = new Dictionary<string, string>
            {
                ["documentText"] = CurrentDocumentText,
                ["groundingContext"] = groundingContext
            };

            var activeDoc = SelectedPersona with
            {
                SystemPrompt = SystemPrompt,
                UserPromptTemplate = UserPromptTemplate
            };

            var messages = _promptyEngine.RenderChatMessages(activeDoc, variables);

            GenerationStatus = "Processing via Foundry Local router...";
            var response = await _router.GetResponseAsync(messages);

            GeneratedSummary = response.Text ?? string.Empty;
            GenerationStatus = $"Summary generated successfully via {LastRoutingInfo?.RouteName ?? "Foundry Local"}";
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
