namespace FoundrySummarizer.Core.Ingestion;

/// <summary>Text extracted from one document, ready to summarize.</summary>
public record IngestionResult
{
    /// <summary>File or display name of the document.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Plain text extracted from the document; empty when it has no extractable text (e.g. a scanned PDF).</summary>
    public string ExtractedText { get; init; } = string.Empty;

    /// <summary>Estimated model tokens (≈4 characters each), shown so users know when a document will be read in parts.</summary>
    public int EstimatedTokens => SemanticChunker.EstimateTokens(ExtractedText);
}
