using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;
using FoundrySummarizer.Core.Verification;
using FoundrySummarizer.Presentation.Services;

namespace FoundrySummarizer.Presentation.ViewModels;

/// <summary>
/// The summarize screen: open a document, pick a summary style, generate the summary with the local model.
/// </summary>
public partial class SummarizerViewModel : ObservableObject
{
    /// <summary>
    /// Ends a summary that stopped at the model's output limit. It is part of the text, so the warning stays with
    /// the summary when it is copied elsewhere.
    /// </summary>
    public const string CutOffMarker = "\n\n[The summary stops here: the model reached its output limit.]";

    private readonly IDocumentIngestionPipeline _ingestion;
    private readonly IDocumentSummarizer _summarizer;
    private readonly IFigureChecker _figureChecker;
    private readonly IDocumentPicker _documentPicker;
    private readonly IClipboardService _clipboard;
    private readonly SummarizationConfig _summarizationConfig;
    private readonly IModelReadiness _modelReadiness;
    private readonly IActivityTracker _activity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    [NotifyCanExecuteChangedFor(nameof(GenerateSummaryCommand))]
    private string _documentText = string.Empty;

    [ObservableProperty]
    private string _documentName = string.Empty;

    [ObservableProperty]
    private int _estimatedTokens;

    [ObservableProperty]
    private PromptyDocument? _selectedPersona;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand))]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDocumentCommand))]
    private bool _isLoadingDocument;

    /// <summary>
    /// Whether the figures in the summary appear in the document, e.g. "⚠️ 1 of 9 figures does not appear…";
    /// empty when there is no summary or it contains no figures.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFigureCheck))]
    private string _figureCheckText = string.Empty;

    /// <summary>True when some figures in the summary were not found in the document.</summary>
    [ObservableProperty]
    private bool _hasUnverifiedFigures;

    [ObservableProperty]
    private string _status = "Open a document to get started.";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Summary styles defined by the built-in Prompty personas.</summary>
    public ObservableCollection<PromptyDocument> Personas { get; }

    /// <summary>True when a document with extractable text is loaded.</summary>
    public bool HasDocument => !string.IsNullOrWhiteSpace(DocumentText);

    /// <summary>True when a summary has been generated for the loaded document.</summary>
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    /// <summary>True when there is a figure check to show.</summary>
    public bool HasFigureCheck => FigureCheckText.Length > 0;

    /// <summary>Raised after a document is loaded, with its name and text. Its previous summary has been cleared.</summary>
    public event Action<string, string>? DocumentLoaded;

    /// <summary>Raised after a summary is generated for the loaded document.</summary>
    public event Action<string>? SummaryGenerated;

    /// <param name="ingestion">Extracts text from documents.</param>
    /// <param name="promptyEngine">Supplies the summary styles.</param>
    /// <param name="summarizer">Writes summaries (splitting long documents into parts).</param>
    /// <param name="figureChecker">Checks that the summary's figures come from the document.</param>
    /// <param name="documentPicker">Asks the user for a file.</param>
    /// <param name="clipboard">Copies the summary.</param>
    /// <param name="summarizationConfig">Used to tell the user when a document will be read in parts.</param>
    /// <param name="modelReadiness">Summarizing is disabled until a model is loaded.</param>
    /// <param name="activity">Marks a running summary, so the model is not switched meanwhile.</param>
    public SummarizerViewModel(
        IDocumentIngestionPipeline ingestion,
        IPromptyEngine promptyEngine,
        IDocumentSummarizer summarizer,
        IFigureChecker figureChecker,
        IDocumentPicker documentPicker,
        IClipboardService clipboard,
        SummarizationConfig summarizationConfig,
        IModelReadiness modelReadiness,
        IActivityTracker activity)
    {
        _modelReadiness = modelReadiness;
        _activity = activity;
        _modelReadiness.ReadinessChanged += (_, _) =>
        {
            GenerateSummaryCommand.NotifyCanExecuteChanged();
            OpenDocumentCommand.NotifyCanExecuteChanged();
        };

        // Open and Copy depend on whether a summary is running. The command only reports that it stopped after the
        // summary method has returned, so the buttons are re-checked then; checking in the method's own finally block
        // would still see it running and leave both buttons disabled.
        GenerateSummaryCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(GenerateSummaryCommand.IsRunning)) return;
            OpenDocumentCommand.NotifyCanExecuteChanged();
            CopySummaryCommand.NotifyCanExecuteChanged();
        };

        _ingestion = ingestion;
        _summarizer = summarizer;
        _figureChecker = figureChecker ?? throw new ArgumentNullException(nameof(figureChecker));
        _documentPicker = documentPicker;
        _clipboard = clipboard;
        _summarizationConfig = summarizationConfig;

        Personas = new ObservableCollection<PromptyDocument>(promptyEngine.AvailablePersonas);
        SelectedPersona = Personas.FirstOrDefault();
    }

    /// <summary>Lets the user pick a file, then loads it.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenDocument))]
    private async Task OpenDocumentAsync()
    {
        var path = _documentPicker.PickDocument(_ingestion.SupportedExtensions);
        if (path is not null)
        {
            await LoadDocumentAsync(path);
        }
    }

    // Like every other action, opening a document waits until a model is loaded.
    private bool CanOpenDocument() => !IsLoadingDocument && !GenerateSummaryCommand.IsRunning && _modelReadiness.IsModelReady;

    private bool CanGenerateSummary() => HasDocument && _modelReadiness.IsModelReady;

    /// <summary>
    /// Extracts the text of <paramref name="filePath"/> and makes it the document to summarize, clearing the
    /// previous document's summary so a stale summary is never shown next to a new document.
    /// </summary>
    /// <param name="filePath">Full path of a supported document.</param>
    public async Task LoadDocumentAsync(string filePath)
    {
        IsLoadingDocument = true;
        SetStatus($"Reading {Path.GetFileName(filePath)}...");
        try
        {
            var result = await _ingestion.IngestFileAsync(filePath);
            DocumentName = result.FileName;
            DocumentText = result.ExtractedText;
            EstimatedTokens = result.EstimatedTokens;
            Summary = string.Empty;
            ShowFigureCheck(FigureCheckResult.Empty);

            if (!HasDocument)
            {
                SetStatus($"No text could be extracted from {result.FileName}. If it is a scanned PDF, run OCR on it first.", isError: true);
                return;
            }

            var length = result.EstimatedTokens > _summarizationConfig.MaxSinglePassTokens
                ? $"~{result.EstimatedTokens:N0} tokens, will be read in parts"
                : $"~{result.EstimatedTokens:N0} tokens";
            SetStatus($"Loaded {result.FileName} ({length}). Choose a summary style and click Summarize.");
            DocumentLoaded?.Invoke(DocumentName, DocumentText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or DocumentReadException)
        {
            SetStatus($"Could not read {Path.GetFileName(filePath)}: {ex.Message}", isError: true);
        }
        finally
        {
            IsLoadingDocument = false;
        }
    }

    /// <summary>
    /// Summarizes the loaded document in the selected style, showing the summary as the model writes it.
    /// Cancellable via <c>GenerateSummaryCancelCommand</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerateSummary), IncludeCancelCommand = true)]
    private async Task GenerateSummaryAsync(CancellationToken cancellationToken)
    {
        if (SelectedPersona is null)
        {
            SetStatus("Choose a summary style first.", isError: true);
            return;
        }

        using var busy = _activity.Begin();
        SetStatus($"Summarizing with {SelectedPersona.Name}...");

        // The new summary replaces the old one as it is written. If it fails or is cancelled, the previous complete
        // summary comes back, so a half-written summary is never left on screen looking finished.
        var previousSummary = Summary;
        Summary = string.Empty;
        var completed = false;
        try
        {
            // Stopped before the final status and summary are set, so a late report cannot overwrite them.
            var progress = new StoppableProgress<SummarizationProgress>(report =>
            {
                SetStatus(report.Status);
                if (report.Draft.Length > 0) Summary = report.Draft;
            });
            SummarizationResult result;
            try
            {
                result = await _summarizer.SummarizeAsync(new SummarizationRequest(SelectedPersona, DocumentText), progress, cancellationToken);
            }
            finally
            {
                progress.Stop();
            }

            Summary = result.WasCutOff ? result.Summary.TrimEnd() + CutOffMarker : result.Summary;
            ShowFigureCheck(_figureChecker.Check(result.Summary, DocumentText));
            completed = true;
            SetStatus(DescribeResult(result), isError: result.WasCutOff || result.CutOffNoteParts > 0);
            SummaryGenerated?.Invoke(Summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("Summary cancelled.");
        }
        catch (LocalModelUnavailableException ex)
        {
            SetStatus($"⚠️ No summary: {ex.Message}", isError: true);
        }
        finally
        {
            if (!completed) Summary = previousSummary;
        }
    }

    /// <summary>
    /// Shows whether the summary's figures appear in the document. The check runs on the original document, not on
    /// the notes a long document's summary is written from, so a figure garbled in the notes is caught too.
    /// </summary>
    private void ShowFigureCheck(FigureCheckResult check)
    {
        HasUnverifiedFigures = !check.AllFound;
        FigureCheckText = check.Checked.Count == 0
            ? string.Empty
            : check.AllFound
                ? FigureCheckMessages.Confirmation(check, "summary")
                : FigureCheckMessages.Warning(check, "summary");
    }

    /// <summary>What was produced, plus a warning for anything that stopped at the model's output limit.</summary>
    private static string DescribeResult(SummarizationResult result)
    {
        var done = result.UsedMultiPart
            ? $"Summary written from {result.PartCount} parts of the document. Ask follow-up questions in the Chat tab."
            : "Summary ready. Ask follow-up questions in the Chat tab.";

        var warnings = new List<string>();
        if (result.WasCutOff)
        {
            warnings.Add("The summary stopped at the model's output limit, so its end is missing. Summarize again, or choose a larger model in the Model list.");
        }

        if (result.CutOffNoteParts > 0)
        {
            warnings.Add(result.CutOffNoteParts == 1
                ? "The notes on 1 part of the document reached the output limit even in smaller pieces, so some of its details may be missing."
                : $"The notes on {result.CutOffNoteParts} parts of the document reached the output limit even in smaller pieces, so some of their details may be missing.");
        }

        return warnings.Count == 0 ? done : $"⚠️ {string.Join(" ", warnings)} {done}";
    }

    // A summary that is still being written is not copied: it would look complete once pasted.
    private bool CanCopySummary() => HasSummary && !GenerateSummaryCommand.IsRunning;

    /// <summary>Copies the summary to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanCopySummary))]
    private void CopySummary()
    {
        var problem = _clipboard.TrySetText(Summary);
        SetStatus(problem ?? "Summary copied to the clipboard.", isError: problem is not null);
    }

    private void SetStatus(string message, bool isError = false)
    {
        Status = message;
        IsError = isError;
    }
}
