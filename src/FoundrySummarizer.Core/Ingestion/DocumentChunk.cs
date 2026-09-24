namespace FoundrySummarizer.Core.Ingestion;

/// <summary>One piece of a document, small enough to send to the model or to rank for a question.</summary>
/// <param name="Id">Unique id: the document id plus the chunk index.</param>
/// <param name="DocumentId">The document this chunk belongs to.</param>
/// <param name="Index">Position in the document, starting at 0.</param>
/// <param name="Text">The chunk's text.</param>
/// <param name="TokenCount">Estimated size in tokens (see <see cref="SemanticChunker.EstimateTokens"/>).</param>
public record DocumentChunk(
    string Id,
    string DocumentId,
    int Index,
    string Text,
    int TokenCount
);
