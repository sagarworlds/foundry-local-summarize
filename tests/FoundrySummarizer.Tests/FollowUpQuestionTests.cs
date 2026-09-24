using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Chat;

namespace FoundrySummarizer.Tests;

public class FollowUpQuestionTests
{
    /// <summary>Returns a fixed answer and records what was asked.</summary>
    private sealed class FixedAnswerClient(string answer) : IChatClient
    {
        public List<IList<ChatMessage>> Calls { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task AsksAboutTheSummary_AndReturnsCleanQuestions()
    {
        var client = new FixedAnswerClient("""
            Here are some questions:
            1. Who approves the $150,000 GPU server request?
            - When is the redlined vendor contract due?
            "Why does the 3x liability cap conflict with LEG-104?"
            What is the Phase 2 budget?
            What is the Phase 2 budget?
            Q5: Which team owns the ERP forecast update?
            """);
        var generator = new FollowUpQuestionGenerator(client, count: 4);

        var questions = await generator.SuggestAsync("helios.txt", "Phase 2 needs $150,000 for GPU servers; VP approval by Friday.", "full text");

        Assert.Equal(new[]
        {
            "Who approves the $150,000 GPU server request?",
            "When is the redlined vendor contract due?",
            "Why does the 3x liability cap conflict with LEG-104?",
            "What is the Phase 2 budget?"
        }, questions);

        var prompt = client.Calls.Single()[^1].Text!;
        Assert.Contains("helios.txt", prompt);
        Assert.Contains("Phase 2 needs $150,000", prompt);    // the summary, not the full text
        Assert.DoesNotContain("full text", prompt);
    }

    [Fact]
    public async Task UsesTheDocumentOpeningWhenThereIsNoSummary()
    {
        var client = new FixedAnswerClient("What is the contract term?");
        var document = "MASTER SERVICES AGREEMENT between Contoso and Fabrikam. " + new string('x', 20_000);

        await new FollowUpQuestionGenerator(client).SuggestAsync("msa.txt", summary: "", document);

        var prompt = client.Calls.Single()[^1].Text!;
        Assert.Contains("MASTER SERVICES AGREEMENT between Contoso and Fabrikam.", prompt);
        Assert.True(prompt.Length < 6_000, "the prompt must stay small enough for a 4K-context model");
    }

    [Fact]
    public async Task ReturnsNothingForAnEmptyDocument_WithoutCallingTheModel()
    {
        var client = new FixedAnswerClient("Why?");

        Assert.Empty(await new FollowUpQuestionGenerator(client).SuggestAsync("empty.txt", "", "   "));
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("Is it?", 0)]                                            // too short to be useful
    [InlineData("The budget is $150,000.", 0)]                           // not a question
    [InlineData("<think>What should I ask?</think>", 0)]                 // reasoning, not a suggestion
    [InlineData("1) Who signs the agreement for Fabrikam?", 1)]
    public void ParseQuestions_KeepsOnlyRealQuestions(string line, int expected)
    {
        Assert.Equal(expected, FollowUpQuestionGenerator.ParseQuestions(line, 4).Count);
    }
}
