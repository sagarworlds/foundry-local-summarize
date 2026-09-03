using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace FoundrySummarizer.Core.Ingestion;

public class DocxDocumentParser : IDocumentParser
{
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx"
    };

    public IReadOnlyCollection<string> SupportedExtensions => _extensions;

    public bool CanHandle(string extension) => _extensions.Contains(extension);

    public Task<string> ExtractTextAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        using var wordDoc = WordprocessingDocument.Open(stream, false);
        var mainPart = wordDoc.MainDocumentPart;
        if (mainPart?.Document?.Body != null)
        {
            foreach (var element in mainPart.Document.Body.ChildElements)
            {
                if (element is Paragraph p)
                {
                    var text = p.InnerText.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        sb.AppendLine(text);
                        sb.AppendLine();
                    }
                }
                else if (element is Table table)
                {
                    foreach (var row in table.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>().Select(c => c.InnerText.Trim());
                        sb.AppendLine(string.Join(" | ", cells));
                    }
                    sb.AppendLine();
                }
            }
        }

        return Task.FromResult(sb.ToString().Trim());
    }
}
