using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Grounding;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Evaluation;
using FoundrySummarizer.Core.Summarization;

namespace FoundrySummarizer.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FoundryOptions _foundryOptions;
    private readonly HybridChatClientRouter _router;

    [ObservableProperty]
    private int _selectedTabIndex = 1; // Default to Persona Summarizer for instant productivity

    public bool IsTab0Selected => SelectedTabIndex == 0;
    public bool IsTab1Selected => SelectedTabIndex == 1;
    public bool IsTab2Selected => SelectedTabIndex == 2;
    public bool IsTab3Selected => SelectedTabIndex == 3;
    public bool IsTab4Selected => SelectedTabIndex == 4;
    public bool IsTab5Selected => SelectedTabIndex == 5;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsTab0Selected));
        OnPropertyChanged(nameof(IsTab1Selected));
        OnPropertyChanged(nameof(IsTab2Selected));
        OnPropertyChanged(nameof(IsTab3Selected));
        OnPropertyChanged(nameof(IsTab4Selected));
        OnPropertyChanged(nameof(IsTab5Selected));
    }

    [RelayCommand]
    public void SelectTab(string indexStr)
    {
        if (int.TryParse(indexStr, out int idx))
        {
            SelectedTabIndex = idx;
        }
    }

    [ObservableProperty]
    private bool _isPrivacyMode = true;

    [ObservableProperty]
    private bool _isDaemonOnline;

    [ObservableProperty]
    private string _daemonStatusText = "Foundry Local: Initializing...";

    [ObservableProperty]
    private string _currentDocumentName = "sample_meeting_transcript.txt";

    public IngestionViewModel IngestionVm { get; }
    public SummarizerViewModel SummarizerVm { get; }
    public GroundingViewModel GroundingVm { get; }
    public AgenticViewModel AgenticVm { get; }
    public ChatViewModel ChatVm { get; }
    public EvaluationViewModel EvaluationVm { get; }

    public MainViewModel(
        FoundryOptions options,
        HybridChatClientRouter router,
        IDocumentIngestionPipeline ingestionPipeline,
        IPromptyEngine promptyEngine,
        IVectorGroundingService groundingService,
        IEvaluationPipeline evaluationPipeline,
        IDocumentSummarizer summarizer)
    {
        _foundryOptions = options;
        _router = router;
        _isPrivacyMode = options.PrivacyMode;

        IngestionVm = new IngestionViewModel(ingestionPipeline);
        SummarizerVm = new SummarizerViewModel(promptyEngine, router, groundingService, summarizer);
        GroundingVm = new GroundingViewModel(groundingService);
        AgenticVm = new AgenticViewModel(router);
        ChatVm = new ChatViewModel(router, options.Chat);
        EvaluationVm = new EvaluationViewModel(evaluationPipeline);

        // Sync ingestion events
        IngestionVm.OnDocumentIngested += result =>
        {
            CurrentDocumentName = result.FileName;
            // Loading clears the previous document's summary, so the tabs below get an empty summary
            // rather than one that belongs to a different document.
            SummarizerVm.LoadDocument(result.FileName, result.ExtractedText);

            AgenticVm.UpdateContext(result.FileName, result.ExtractedText, SummarizerVm.GeneratedSummary);
            EvaluationVm.UpdateContext(result.FileName, SummarizerVm.SelectedPersona?.Name ?? "Executive Bullets", result.ExtractedText, SummarizerVm.GeneratedSummary, SummarizerVm.LastReferenceText);
            ChatVm.InitializeSession(result.FileName, result.ExtractedText, SummarizerVm.GeneratedSummary);
        };

        // "Summarize This Document" on the ingestion screen: go to the summarizer and start generating.
        IngestionVm.OnSummarizeRequested += async () =>
        {
            SelectedTabIndex = 1;
            if (SummarizerVm.GenerateSummaryCommand.CanExecute(null))
            {
                await SummarizerVm.GenerateSummaryCommand.ExecuteAsync(null);
            }
        };

        // Sync summarization events
        SummarizerVm.OnSummaryGenerated += (docName, summary) =>
        {
            AgenticVm.UpdateContext(docName, SummarizerVm.CurrentDocumentText, summary);
            EvaluationVm.UpdateContext(docName, SummarizerVm.SelectedPersona?.Name ?? "Executive Bullets", SummarizerVm.CurrentDocumentText, summary, SummarizerVm.LastReferenceText);
            ChatVm.InitializeSession(docName, SummarizerVm.CurrentDocumentText, summary);
        };

        // Initialize default sample document
        LoadDefaultSample();

        // Check local daemon ping
        Task.Run(CheckDaemonStatusAsync);
    }

    partial void OnIsPrivacyModeChanged(bool value)
    {
        _foundryOptions.PrivacyMode = value;
        DaemonStatusText = value
            ? $"🔒 Privacy Mode: Local ({_foundryOptions.LocalEndpoint}, $0.00)"
            : $"☁️ Hybrid Mode: Local + Cloud ({_foundryOptions.CloudModelId})";
    }

    [RelayCommand]
    public async Task CheckDaemonStatusAsync()
    {
        bool online = await _router.CheckLocalDaemonStatusAsync();
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsDaemonOnline = online;
            if (IsPrivacyMode)
            {
                DaemonStatusText = online
                    ? $"Foundry Local: Active ({_foundryOptions.LocalEndpoint}, model: {_router.ActiveLocalModelId})"
                    : $"Foundry Local: Offline Mode Ready ({_foundryOptions.LocalModelId})";
            }
            else
            {
                DaemonStatusText = $"☁️ Hybrid Mode ({_foundryOptions.CloudModelId} / {_router.ActiveLocalModelId})";
            }
        });
    }

    [RelayCommand]
    public async Task LoadSampleAsync(string sampleType)
    {
        string baseDir = AppContext.BaseDirectory;
        string fileName = sampleType.ToLowerInvariant() switch
        {
            "meeting" => "sample_meeting_transcript.txt",
            "contract" => "sample_contract.txt",
            "proposal" => "sample_project_proposal.docx",
            "financial" => "sample_financial_proposal.txt",
            "deck" => "sample_executive_deck.pptx",
            _ => "sample_meeting_transcript.txt"
        };

        var candidatePaths = new[]
        {
            Path.Combine(baseDir, "samples", fileName),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "samples", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "samples", fileName),
            Path.Combine(baseDir, fileName)
        };

        string? targetFile = candidatePaths.FirstOrDefault(File.Exists);

        if (targetFile != null)
        {
            await IngestionVm.IngestFileAsync(targetFile);
            SelectedTabIndex = 1; // Switch to summarizer view
            await SummarizerVm.GenerateSummaryAsync();
        }
        else
        {
            // Fallback to embedded default text
            var defaultText = """
            EXECUTIVE MEETING TRANSCRIPT: PROJECT HELIOS REVIEW
            DATE: August 28, 2026
            PARTICIPANTS: Marcus Vance (VP Tech), David Chen (Architect), Sarah Jenkins (Finance), Elena Rostova (Legal).

            David: Phase 1 complete. Phase 2 requires $150,000 for local GPU acceleration hardware servers.
            Sarah: Any expenditure over $100,000 requires VP approval per the Q2 Financial Framework.
            Marcus: Approved. Submit the formal request to me by Friday.
            Elena: On vendor contracts, liability caps must not exceed 1x contract value. I will redline the 3x clause.
            """;
            await IngestionVm.IngestTextContentAsync(defaultText, "Sample_Meeting_Review.txt");
            SelectedTabIndex = 1;
            await SummarizerVm.GenerateSummaryAsync();
        }
    }

    private void LoadDefaultSample()
    {
        var defaultText = """
        EXECUTIVE MEETING TRANSCRIPT: PROJECT HELIOS REVIEW
        DATE: August 28, 2026 | PARTICIPANTS: Marcus Vance (VP Tech), David Chen (Architect), Sarah Jenkins (Finance), Elena Rostova (Legal).

        [00:00:10] Marcus: Let's begin the review for Project Helios modernization. David, status?
        [00:00:35] David: Phase 1 refactoring is 100% complete. For Phase 2, we need dedicated local GPU acceleration servers to run Foundry Local models offline. The quote is $150,000.
        [00:01:15] Sarah: David, from our Q2 Financial Framework, any expenditure over $100,000 triggers mandatory VP approval.
        [00:01:45] Marcus: Understood. David, submit the formal authorization request to my office by Friday.
        [00:02:10] Elena: Regarding vendor terms, the supplier proposed a 3x contract liability cap. Corporate policy LEG-104 limits liability to 1x contract value. I will redline by Tuesday.
        [00:02:40] Sarah: I will update the ERP budget forecast once approved.
        """;

        CurrentDocumentName = "sample_meeting_transcript.txt";
        SummarizerVm.LoadDocument("sample_meeting_transcript.txt", defaultText);

        _ = IngestionVm.IngestTextContentAsync(defaultText, "sample_meeting_transcript.txt");
        _ = SummarizerVm.GenerateSummaryAsync();
    }
}
