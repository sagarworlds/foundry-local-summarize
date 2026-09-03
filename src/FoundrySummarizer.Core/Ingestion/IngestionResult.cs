namespace FoundrySummarizer.Core.Ingestion;

public record IngestionResult
{
    public string DocumentId { get; init; } = Guid.NewGuid().ToString("N");
    public string FileName { get; init; } = string.Empty;
    public string FileExtension { get; init; } = string.Empty;
    public string ExtractedText { get; init; } = string.Empty;
    public int CharacterCount => ExtractedText.Length;
    public int EstimatedTokens => (int)Math.Ceiling(CharacterCount / 4.0);
    public IReadOnlyList<DocumentChunk> Chunks { get; init; } = Array.Empty<DocumentChunk>();
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
