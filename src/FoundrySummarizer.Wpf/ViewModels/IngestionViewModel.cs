using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using FoundrySummarizer.Core.Ingestion;

namespace FoundrySummarizer.Wpf.ViewModels;

public partial class IngestionViewModel : ObservableObject
{
    private readonly IDocumentIngestionPipeline _pipeline;

    [ObservableProperty]
    private string _selectedFilePath = string.Empty;

    [ObservableProperty]
    private string _extractedText = string.Empty;

    [ObservableProperty]
    private int _characterCount;

    [ObservableProperty]
    private int _estimatedTokens;

    [ObservableProperty]
    private string _statusMessage = "Ready to ingest documents (.docx, .pptx, .pdf, .txt, .mp3, .wav)";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SummarizeDocumentCommand))]
    private bool _hasDocument;

    public ObservableCollection<DocumentChunk> Chunks { get; } = new();

    public event Action<IngestionResult>? OnDocumentIngested;

    /// <summary>Raised when the user asks to summarize the ingested document from this screen.</summary>
    public event Action? OnSummarizeRequested;

    /// <summary>Hands the ingested document to the summarizer (the shell switches tabs and starts generation).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    public void SummarizeDocument() => OnSummarizeRequested?.Invoke();

    public IngestionViewModel(IDocumentIngestionPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    [RelayCommand]
    public async Task BrowseFileAsync()
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = "Select Document or Audio File for Ingestion",
            Filter = "All Supported Files (*.docx;*.pptx;*.pdf;*.txt;*.mp3;*.wav)|*.docx;*.pptx;*.pdf;*.txt;*.mp3;*.wav|" +
                     "Word Documents (*.docx)|*.docx|" +
                     "PowerPoint Decks (*.pptx)|*.pptx|" +
                     "PDF Files (*.pdf)|*.pdf|" +
                     "Audio Files (*.mp3;*.wav)|*.mp3;*.wav|" +
                     "Text & Markdown (*.txt;*.md;*.json)|*.txt;*.md;*.json|" +
                     "All Files (*.*)|*.*"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            await IngestFileAsync(openFileDialog.FileName);
        }
    }

    public async Task IngestFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        IsBusy = true;
        StatusMessage = $"Ingesting and chunking {Path.GetFileName(filePath)}...";
        SelectedFilePath = filePath;

        try
        {
            var result = await _pipeline.IngestFileAsync(filePath);
            ExtractedText = result.ExtractedText;
            CharacterCount = result.CharacterCount;
            EstimatedTokens = result.EstimatedTokens;

            Chunks.Clear();
            foreach (var chunk in result.Chunks)
            {
                Chunks.Add(chunk);
            }

            HasDocument = !string.IsNullOrWhiteSpace(result.ExtractedText);
            StatusMessage = HasDocument
                ? $"Successfully ingested {result.FileName} ({result.Chunks.Count} chunks, ~{result.EstimatedTokens:N0} tokens). Click 'Summarize This Document' to continue."
                : $"No text could be extracted from {result.FileName}. If it is a scanned PDF, run OCR on it first.";
            OnDocumentIngested?.Invoke(result);
        }
        catch (Exception ex)
        {
            HasDocument = false;
            StatusMessage = $"Ingestion Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task IngestTextContentAsync(string text, string documentName)
    {
        IsBusy = true;
        StatusMessage = $"Ingesting text for {documentName}...";

        try
        {
            var result = await _pipeline.IngestTextAsync(text, documentName);
            ExtractedText = result.ExtractedText;
            CharacterCount = result.CharacterCount;
            EstimatedTokens = result.EstimatedTokens;
            SelectedFilePath = documentName;

            Chunks.Clear();
            foreach (var chunk in result.Chunks)
            {
                Chunks.Add(chunk);
            }

            HasDocument = !string.IsNullOrWhiteSpace(result.ExtractedText);
            StatusMessage = $"Successfully ingested {documentName} ({result.Chunks.Count} chunks, ~{result.EstimatedTokens:N0} tokens)";
            OnDocumentIngested?.Invoke(result);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
