using Microsoft.Win32;

namespace FoundrySummarizer.Wpf.Services;

/// <summary>Picks a document with the standard Windows "Open" dialog.</summary>
public sealed class OpenFileDocumentPicker : IDocumentPicker
{
    /// <inheritdoc />
    public string? PickDocument(IReadOnlyCollection<string> supportedExtensions)
    {
        var patterns = string.Join(";", supportedExtensions.Order(StringComparer.OrdinalIgnoreCase).Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Open a document to summarize",
            Filter = $"Supported documents ({patterns})|{patterns}|" +
                     "Word documents (*.docx)|*.docx|" +
                     "PowerPoint decks (*.pptx)|*.pptx|" +
                     "PDF files (*.pdf)|*.pdf|" +
                     "Text files (*.txt;*.md)|*.txt;*.md"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
