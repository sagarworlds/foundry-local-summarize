namespace FoundrySummarizer.Core.Ingestion;

public record DocumentChunk(
    string Id,
    string DocumentId,
    int Index,
    string Text,
    int TokenCount,
    Dictionary<string, string>? Metadata = null
);
