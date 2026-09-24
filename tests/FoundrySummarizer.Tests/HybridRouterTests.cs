using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

public class HybridRouterTests
{
    [Fact]
    public async Task Router_EnforcesPrivacyMode_ZeroCostAndLocal()
    {
        var options = new FoundryOptions
        {
            PrivacyMode = true,
            LocalEndpoint = "http://localhost:5272/v1"
        };

        var router = new HybridChatClientRouter(options);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Persona: Executive Bullets"),
            new(ChatRole.User, "Project Helios budget request of $150,000 for local GPU server.")
        };

        var response = await router.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.NotNull(router.LastRoutingDecision);
        Assert.True(router.LastRoutingDecision.IsPrivacyEnforced);
        Assert.True(router.LastRoutingDecision.IsLocal);
        Assert.Equal(0.00m, router.LastRoutingDecision.EstimatedCostUsd);
        Assert.Contains("Privacy Mode Active", router.LastRoutingDecision.Rationale);
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }

    [Fact]
    public async Task Router_LabelsOfflineDemoOutput_WhenNoModelIsReachable()
    {
        // Port 9 (discard) is never an OpenAI-compatible endpoint, so the router must use the demo engine.
        var options = new FoundryOptions { PrivacyMode = true };
        options.Local.AutoDiscover = false;
        options.Local.Endpoint = "http://127.0.0.1:9/v1";

        var router = new HybridChatClientRouter(options);
        var response = await router.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.System, "Persona: Executive Bullets"),
            new(ChatRole.User, "Budget request of $150,000.")
        });

        Assert.StartsWith(HybridChatClientRouter.FallbackNotice, response.Text);
    }

    [Fact]
    public async Task FallbackClient_GeneratesActionItemsSummary()
    {
        var client = new LocalFoundryFallbackClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Persona: Action-Item Extractor"),
            new(ChatRole.User, "David Chen must submit the VP approval. Elena to review vendor contracts.")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.Contains("Action Items Breakdown", response.Text);
        Assert.Contains("David (Engineering)", response.Text);
        Assert.Contains("Elena (Legal)", response.Text);
    }

    [Fact]
    public async Task FallbackClient_GeneratesLegalSummary()
    {
        var client = new LocalFoundryFallbackClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Persona: Legal Compliance Check"),
            new(ChatRole.User, "Master Services Agreement between Global Enterprise and Vendor with $85,000 fee and 3x liability cap.")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.Contains("Liability Exposures", response.Text);
        Assert.Contains("Termination, Cure Periods", response.Text);
        Assert.Contains("Red Flag Warnings", response.Text);
    }
}
