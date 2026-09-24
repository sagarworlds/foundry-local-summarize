using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

/// <summary>
/// Runs a test only against a real Foundry Local, when FOUNDRY_LOCAL_TESTS=1 is set; otherwise it is reported as
/// skipped. CI runners have no Foundry Local (or GPU), so these tests are for a developer machine.
/// </summary>
public sealed class LocalFoundryFactAttribute : FactAttribute
{
    /// <summary>The environment variable that turns these tests on.</summary>
    public const string Switch = "FOUNDRY_LOCAL_TESTS";

    public LocalFoundryFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Switch) != "1")
        {
            Skip = $"Needs a real Foundry Local with a downloaded chat model; set {Switch}=1 to run.";
        }
    }
}

/// <summary>
/// End-to-end checks against the Foundry Local installed on this machine: the same discovery, model loading and
/// chat path the app uses, with no fakes. Run with:
/// <code>FOUNDRY_LOCAL_TESTS=1 dotnet test --filter LocalFoundryIntegrationTests</code>
/// (PowerShell: <c>$env:FOUNDRY_LOCAL_TESTS=1; dotnet test --filter LocalFoundryIntegrationTests</c>).
/// </summary>
public class LocalFoundryIntegrationTests
{
    private static FoundryOptions Options()
    {
        var options = new FoundryOptions();
        options.Local.PreferredModels = new[] { "phi-4-mini", "qwen2.5-1.5b", "qwen2.5-0.5b" };
        return options;
    }

    [LocalFoundryFact]
    public async Task FindsTheService_ListsDownloadedModels_AndLoadsOne()
    {
        using var client = new FoundryLocalChatClient(Options());

        var list = await client.ListModelsAsync();
        Assert.True(list.Models.Count > 0, list.Problem ?? "No downloaded chat models. Run 'foundry model download phi-4-mini'.");

        var status = await client.LoadActiveModelAsync();
        Assert.True(status.IsAvailable, status.Problem);
        Assert.Equal(ActiveModelStateKind.Loaded, (await client.GetActiveModelStateAsync()).Kind);
    }

    [LocalFoundryFact]
    public async Task AnswersAQuestion_WithoutReasoningText()
    {
        using var client = new FoundryLocalChatClient(Options());

        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Reply with the single word OK.") },
            new ChatOptions { MaxOutputTokens = 20, Temperature = 0 });

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.DoesNotContain("<think>", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [LocalFoundryFact]
    public async Task AnswersFromTheDocument_AndSuggestsQuestionsAboutIt()
    {
        using var client = new FoundryLocalChatClient(Options());
        var document = "Project Atlas minutes. The approved budget is $150,000. Priya Shah owns the data migration, due on 30 June.";

        var agent = new DocumentChatAgent(client);
        agent.InitializeSession("atlas.txt", document, summaryText: string.Empty);
        var answer = await agent.AskQuestionAsync("What is the approved budget?");
        Assert.Contains("150", answer);

        var questions = await new FollowUpQuestionGenerator(client).SuggestAsync("atlas.txt", string.Empty, document);
        Assert.NotEmpty(questions);
        Assert.All(questions, q => Assert.EndsWith("?", q));
    }
}
