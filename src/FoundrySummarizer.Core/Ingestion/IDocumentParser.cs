namespace FoundrySummarizer.Core.Ingestion;

public interface IDocumentParser
{
    IReadOnlyCollection<string> SupportedExtensions { get; }
    bool CanHandle(string extension);
    Task<string> ExtractTextAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
}
