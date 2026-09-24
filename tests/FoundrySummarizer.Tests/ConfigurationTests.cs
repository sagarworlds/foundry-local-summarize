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
            "Local": {
              "Endpoint": "http://localhost:11434/v1",
              "ModelId": "llama3.2:3b",
              "AutoDiscover": false,
              "TimeoutSeconds": 15,
              "PreferredModels": [ "phi-4-mini" ]
            },
            "Summarization": { "MaxSinglePassTokens": 6000 },
            "Chat": { "MaxHistoryTurns": 2 }
          }
        }
        """;

        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var options = new FoundryOptions();
        config.GetSection(FoundryOptions.SectionName).Bind(options);

        Assert.Equal("http://localhost:11434/v1", options.Local.Endpoint);
        Assert.Equal("llama3.2:3b", options.LocalModelId);
        Assert.False(options.Local.AutoDiscover);
        Assert.Equal(15, options.Local.TimeoutSeconds);
        Assert.Equal(new[] { "phi-4-mini" }, options.Local.GetPreferredModels());
        Assert.Equal(6000, options.Summarization.MaxSinglePassTokens);
        Assert.Equal(2, options.Chat.MaxHistoryTurns);

        // With discovery off, the configured endpoint is used as-is.
        Assert.Equal("http://localhost:11434/v1", options.GetEffectiveLocalEndpoint());
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
}
