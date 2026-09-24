using Microsoft.Win32;
using FoundrySummarizer.Presentation.Services;

namespace FoundrySummarizer.Wpf.Services;

/// <summary>Picks a document with the standard Windows "Open" dialog.</summary>
public sealed class OpenFileDocumentPicker : IDocumentPicker
{
    /// <inheritdoc />
    public string? PickDocument(IReadOnlyCollection<string> supportedExtensions)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a document to summarize",
            Filter = BuildFilter(supportedExtensions)
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// The dialog's file-type filter: every supported type first (the default choice), then one entry per format.
    /// </summary>
    /// <param name="supportedExtensions">Extensions with a leading dot, e.g. ".pdf".</param>
    /// <returns>A filter in the <see cref="FileDialog.Filter"/> format ("label|patterns|label|patterns…").</returns>
    public static string BuildFilter(IReadOnlyCollection<string> supportedExtensions)
    {
        var patterns = string.Join(";", supportedExtensions.Order(StringComparer.OrdinalIgnoreCase).Select(e => "*" + e));
        return $"Supported documents ({patterns})|{patterns}|" +
               "Word documents (*.docx)|*.docx|" +
               "PowerPoint decks (*.pptx)|*.pptx|" +
               "PDF files (*.pdf)|*.pdf|" +
               "Text files (*.txt;*.md)|*.txt;*.md";
    }
}
