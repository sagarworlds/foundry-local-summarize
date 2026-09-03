using Microsoft.Extensions.VectorData;

namespace FoundrySummarizer.Core.Grounding;

public class GroundingRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [VectorStoreData]
    public string Title { get; set; } = string.Empty;

    [VectorStoreData]
    public string DocumentSource { get; set; } = string.Empty;

    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreData]
    public string Category { get; set; } = string.Empty;

    [VectorStoreVector(384)]
    public ReadOnlyMemory<float> Vector { get; set; }
}

public record GroundingSearchResult(
    GroundingRecord Record,
    double SimilarityScore,
    string Snippet
);
