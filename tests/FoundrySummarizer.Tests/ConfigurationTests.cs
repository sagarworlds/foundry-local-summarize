using System.Text;
using Microsoft.Extensions.Configuration;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

public class ConfigurationTests
{
    [Fact]
    public void FoundryOptions_BindsFromConfigurationJson()
    {
        var json = """
        {
          "Foundry": {
            "PrivacyMode": true,
            "EscalateOnComplexity": true,
            "EscalationTokenThreshold": 3000,
            "Local": {
              "Endpoint": "http://localhost:11434/v1",
              "ModelId": "llama3.2:3b",
              "TimeoutSeconds": 15,
              "Provider": "Ollama"
            },
            "Cloud": {
              "Endpoint": "https://my-foundry.openai.azure.com",
              "ModelId": "gpt-4o-mini",
              "ApiKey": "secret-test-key",
              "Provider": "AzureAIFoundry"
            }
          }
        }
        """;

        var builder = new ConfigurationBuilder();
        builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        var config = builder.Build();

        var options = new FoundryOptions();
        config.GetSection(FoundryOptions.SectionName).Bind(options);

        Assert.True(options.PrivacyMode);
        Assert.True(options.EscalateOnComplexity);
        Assert.Equal(3000, options.EscalationTokenThreshold);

        // Verify Local configuration
        Assert.Equal("http://localhost:11434/v1", options.Local.Endpoint);
        Assert.Equal("llama3.2:3b", options.Local.ModelId);
        Assert.Equal("Ollama", options.Local.Provider);
        Assert.Equal(15, options.Local.TimeoutSeconds);

        // Verify Cloud configuration
        Assert.Equal("https://my-foundry.openai.azure.com", options.Cloud.Endpoint);
        Assert.Equal("gpt-4o-mini", options.Cloud.ModelId);
        Assert.Equal("secret-test-key", options.Cloud.ApiKey);
        Assert.Equal("AzureAIFoundry", options.Cloud.Provider);

        // Verify backward compatibility accessors
        Assert.Equal("http://localhost:11434/v1", options.LocalEndpoint);
        Assert.Equal("llama3.2:3b", options.LocalModelId);
        Assert.Equal("https://my-foundry.openai.azure.com", options.CloudEndpoint);
        Assert.Equal("gpt-4o-mini", options.CloudModelId);
        Assert.Equal("secret-test-key", options.CloudApiKey);
    }

    [Fact]
    public void FoundryOptions_AutoDiscoversLocalDaemonEndpoint()
    {
        var options = new FoundryOptions
        {
            Local = new LocalFoundryConfig
            {
                Endpoint = "auto",
                AutoDiscover = true
            }
        };

        string effective = options.GetEffectiveLocalEndpoint();
        Assert.NotNull(effective);
        Assert.StartsWith("http://", effective);
        Assert.EndsWith("/v1", effective);
    }

    [Fact]
    public async Task HybridRouter_PingsLiveFoundryDaemon()
    {
        var options = new FoundryOptions
        {
            Local = new LocalFoundryConfig
            {
                Endpoint = "auto",
                AutoDiscover = true,
                ModelId = "qwen2.5-0.5b-instruct-generic-cpu"
            }
        };

        var router = new HybridChatClientRouter(options);
        bool isOnline = await router.CheckLocalDaemonStatusAsync();

        // On this machine with Foundry Local running, CheckLocalDaemonStatusAsync should return true!
        Assert.True(isOnline);
    }
}
