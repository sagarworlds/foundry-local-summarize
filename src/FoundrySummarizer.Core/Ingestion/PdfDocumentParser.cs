using System.Text;
using UglyToad.PdfPig;

namespace FoundrySummarizer.Core.Ingestion;

public class PdfDocumentParser : IDocumentParser
{
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public IReadOnlyCollection<string> SupportedExtensions => _extensions;

    public bool CanHandle(string extension) => _extensions.Contains(extension);

    public Task<string> ExtractTextAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        // UglyToad.PdfPig PdfDocument.Open accepts byte array or stream
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        memoryStream.Position = 0;

        using var document = PdfDocument.Open(memoryStream);
        foreach (var page in document.GetPages())
        {
            sb.AppendLine($"--- Page {page.Number} ---");
            var text = page.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                sb.AppendLine(text);
            }
            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString().Trim());
    }
}
