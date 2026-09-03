using System.Text;

namespace FoundrySummarizer.Core.Ingestion;

public class PlainTextParser : IDocumentParser
{
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".log", ".yaml", ".yml", ".xml", ".html"
    };

    public IReadOnlyCollection<string> SupportedExtensions => _extensions;

    public bool CanHandle(string extension) => _extensions.Contains(extension);

    public async Task<string> ExtractTextAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
