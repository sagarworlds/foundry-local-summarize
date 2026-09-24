using FoundrySummarizer.Core.Grounding;

namespace FoundrySummarizer.Tests;

public class VectorGroundingTests
{
    [Fact]
    public void SemanticEmbeddingGenerator_Produces384Dimensions()
    {
        var vec = SemanticEmbeddingGenerator.CreateEmbedding("Quarterly financial budget framework");
        Assert.Equal(384, vec.Length);

        // Verify normalized (magnitude ~ 1.0)
        double sumSq = vec.Sum(v => v * v);
        Assert.InRange(sumSq, 0.99, 1.01);
    }

    [Fact]
    public async Task VectorGroundingService_RetrievesRelevantPolicy()
    {
        var service = new VectorGroundingService();
        Assert.True(service.IndexedPolicies.Count >= 3);

        var query = "We need $150,000 for hardware capital expenditure in Q2.";
        var results = await service.SearchAsync(query, topK: 2);

        Assert.NotEmpty(results);
        var topResult = results[0];
        Assert.Contains("Finance", topResult.Record.Category);
        Assert.Contains("100,000", topResult.Record.Content);
        Assert.True(topResult.SimilarityScore > 0.3);
    }

    [Fact]
    public async Task VectorGroundingService_BuildsGroundingPromptContext()
    {
        var service = new VectorGroundingService();
        var context = await service.BuildGroundingPromptContextAsync("Legal contract liability cap 3x total contract fee.");

        Assert.Contains("SEMANTIC GROUNDING", context);
        Assert.Contains("Liability Cap Standards", context);
        Assert.Contains("LEG-104", context);
        Assert.Contains("<reference_policies>", context);
        Assert.Contains("NOT part of the document", context);
    }
}
