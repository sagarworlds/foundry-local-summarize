using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;
using FoundrySummarizer.Wpf.Services;

namespace FoundrySummarizer.Wpf.ViewModels;

/// <summary>
/// The summarize screen: open a document, pick a summary style, generate the summary with the local model.
/// </summary>
public partial class SummarizerViewModel : ObservableObject
{
    private readonly IDocumentIngestionPipeline _ingestion;
    private readonly IDocumentSummarizer _summarizer;
    private readonly IDocumentPicker _documentPicker;
    private readonly IClipboardService _clipboard;
    private readonly SummarizationConfig _summarizationConfig;

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

    /// <summary>Raised after a document is loaded, with its name and text. Its previous summary has been cleared.</summary>
    public event Action<string, string>? DocumentLoaded;

    /// <summary>Raised after a summary is generated for the loaded document.</summary>
    public event Action<string>? SummaryGenerated;

    /// <param name="ingestion">Extracts text from documents.</param>
    /// <param name="promptyEngine">Supplies the summary styles.</param>
    /// <param name="summarizer">Writes summaries (splitting long documents into parts).</param>
    /// <param name="documentPicker">Asks the user for a file.</param>
    /// <param name="clipboard">Copies the summary.</param>
    /// <param name="summarizationConfig">Used to tell the user when a document will be read in parts.</param>
    public SummarizerViewModel(
        IDocumentIngestionPipeline ingestion,
        IPromptyEngine promptyEngine,
        IDocumentSummarizer summarizer,
        IDocumentPicker documentPicker,
        IClipboardService clipboard,
        SummarizationConfig summarizationConfig)
    {
        _ingestion = ingestion;
        _summarizer = summarizer;
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

    private bool CanOpenDocument() => !IsLoadingDocument && !GenerateSummaryCommand.IsRunning;

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

    /// <summary>Summarizes the loaded document in the selected style. Cancellable via <c>GenerateSummaryCancelCommand</c>.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument), IncludeCancelCommand = true)]
    private async Task GenerateSummaryAsync(CancellationToken cancellationToken)
    {
        if (SelectedPersona is null)
        {
            SetStatus("Choose a summary style first.", isError: true);
            return;
        }

        OpenDocumentCommand.NotifyCanExecuteChanged();
        SetStatus($"Summarizing with {SelectedPersona.Name}...");
        try
        {
            // Progress<T> captures the UI synchronization context, so status updates are marshalled safely.
            var progress = new Progress<string>(message => SetStatus(message));
            var result = await _summarizer.SummarizeAsync(new SummarizationRequest(SelectedPersona, DocumentText), progress, cancellationToken);

            Summary = result.Summary;
            SetStatus(result.UsedMultiPart
                ? $"Summary written from {result.PartCount} parts of the document. Ask follow-up questions in the Chat tab."
                : "Summary ready. Ask follow-up questions in the Chat tab.");
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
            OpenDocumentCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Copies the summary to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(HasSummary))]
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
