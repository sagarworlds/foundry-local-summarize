using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

public class ModelSelectionTests
{
    private const string Configured = "qwen2.5-0.5b-instruct-generic-cpu";

    [Fact]
    public void Select_PrefersHighestRankedAvailableModel()
    {
        var available = new[] { Configured, "phi-3.5-mini-instruct-generic-cpu", "Phi-4-mini-instruct-generic-cpu" };

        var selected = LocalModelSelector.Select(available, LocalModelSelector.DefaultPreferences, Configured);

        Assert.Equal("Phi-4-mini-instruct-generic-cpu", selected);
    }

    [Fact]
    public void Select_PrefersGpuBuildOfSameModel()
    {
        var available = new[] { "phi-4-mini-instruct-generic-cpu", "phi-4-mini-instruct-generic-gpu" };

        Assert.Equal("phi-4-mini-instruct-generic-gpu", LocalModelSelector.Select(available, LocalModelSelector.DefaultPreferences, Configured));
    }

    [Fact]
    public void Select_SkipsReasoningAndCoderVariants()
    {
        var available = new[] { "phi-4-mini-reasoning-generic-gpu", "qwen2.5-coder-7b-instruct-generic-gpu", Configured };

        Assert.Equal(Configured, LocalModelSelector.Select(available, LocalModelSelector.DefaultPreferences, Configured));
    }

    [Fact]
    public void Select_KeepsConfiguredModelWhenNothingPreferredIsAvailable()
    {
        Assert.Equal("llama3.2:3b", LocalModelSelector.Select(Array.Empty<string>(), LocalModelSelector.DefaultPreferences, "llama3.2:3b"));
    }

    [Fact]
    public void Select_DoesNotMatchLongerAliasSharingAPrefix()
    {
        // "qwen2.5-7b" must not be satisfied by a hypothetical "qwen2.5-7bx" id.
        Assert.Equal(Configured, LocalModelSelector.Select(new[] { "qwen2.5-7bx" }, new[] { "qwen2.5-7b" }, Configured));
    }

    [Fact]
    public void LocalConfig_UsesBuiltInPreferencesUntilConfigured()
    {
        var config = new LocalFoundryConfig();
        Assert.Equal(LocalModelSelector.DefaultPreferences, config.GetPreferredModels());

        config.PreferredModels = new[] { "my-model" };
        Assert.Equal(new[] { "my-model" }, config.GetPreferredModels());
    }

    [Theory]
    [InlineData("""{"object":"list","data":[{"id":"phi-4-mini-instruct-generic-gpu"},{"id":"qwen2.5-0.5b"}]}""")]
    [InlineData("""["phi-4-mini-instruct-generic-gpu","qwen2.5-0.5b"]""")]
    [InlineData("""[{"name":"phi-4-mini-instruct-generic-gpu"},{"name":"qwen2.5-0.5b"}]""")]
    public void Catalog_ParsesKnownModelListShapes(string json)
    {
        Assert.Equal(new[] { "phi-4-mini-instruct-generic-gpu", "qwen2.5-0.5b" }, LocalModelCatalog.ParseModelIds(json));
    }

    [Fact]
    public void Catalog_ReturnsEmptyForUnrecognisedPayload()
    {
        Assert.Empty(LocalModelCatalog.ParseModelIds("""{"status":"ok"}"""));
        Assert.Empty(LocalModelCatalog.ParseModelIds(""));
    }

    [Fact]
    public void Router_EscalatesOnlyWhenPrivacyOffAndCloudConfigured()
    {
        var options = new FoundryOptions { PrivacyMode = false, EscalationTokenThreshold = 1000 };
        options.Cloud.ApiKey = "test-key";
        var router = new HybridChatClientRouter(options);

        Assert.True(router.WouldEscalateToCloud(5000));
        Assert.False(router.WouldEscalateToCloud(500));

        options.PrivacyMode = true;
        Assert.False(router.WouldEscalateToCloud(5000));
    }
}
