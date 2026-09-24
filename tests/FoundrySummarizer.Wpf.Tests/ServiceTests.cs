using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Wpf.Services;

namespace FoundrySummarizer.Wpf.Tests;

public class ServiceTests
{
    [Fact]
    public void OpenDialogFilter_OffersEverySupportedTypeFirst_ThenEachFormat()
    {
        var filter = OpenFileDocumentPicker.BuildFilter(new DocumentIngestionPipeline().SupportedExtensions);
        var parts = filter.Split('|');

        Assert.Equal(0, parts.Length % 2);                                // label|pattern pairs
        Assert.StartsWith("Supported documents (", parts[0]);
        foreach (var extension in new[] { ".docx", ".pptx", ".pdf", ".txt", ".md" })
        {
            Assert.Contains("*" + extension, parts[1].Split(';'));
        }

        Assert.Contains("PDF files (*.pdf)", parts);
    }

    [Fact]
    public void Clipboard_CopiesText_OrSaysWhyNot()
    {
        // The clipboard needs an STA thread; another app may hold it, in which case a reason is returned, never thrown.
        var (problem, copied) = UiThread.Invoke(() =>
        {
            var result = new WpfClipboardService().TrySetText("Budget: $150,000");
            return (result, result is null ? System.Windows.Clipboard.GetText() : null);
        });

        if (problem is null)
        {
            Assert.Equal("Budget: $150,000", copied);
        }
        else
        {
            Assert.Contains("clipboard is in use", problem);
        }
    }
}
