using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

public class ChatRetrievalTests
{
    private static readonly (string Topic, string Owner, string Amount)[] Workstreams =
    {
        ("data migration", "Priya Shah", "$42,000"),
        ("security audit", "Marcus Lee", "$18,500"),
        ("vendor onboarding", "Elena Ruiz", "$9,750"),
        ("office relocation", "Tom Becker", "$120,000"),
        ("payroll upgrade", "Aisha Khan", "$27,300"),
        ("network refresh", "Liam Ortiz", "$64,900"),
        ("helpdesk staffing", "Nora Klein", "$15,200"),
        ("warehouse robotics", "Omar Haddad", "$310,000"),
        ("brand campaign", "Sofia Rossi", "$55,000"),
        ("compliance training", "Kenji Sato", "$7,400")
    };

    // Each section is ~30 tokens, so a 60-token passage holds about two sections.
    private static string BuildDocument() => string.Join("\n\n", Workstreams.Select((w, i) =>
        $"Section {i + 1}: The {w.Topic} workstream is led by {w.Owner}. Its approved budget is {w.Amount} for this fiscal year."));

    private static Bm25PassageRetriever SmallPassageRetriever() => new(new SemanticChunker(maxTokensPerChunk: 60, overlapTokens: 0));

    [Fact]
    public void Retriever_FindsThePassageNamingTheTopic()
    {
        var retriever = SmallPassageRetriever();
        retriever.Index(BuildDocument());

        var passages = retriever.Retrieve("Who leads the data migration?", maxTokens: 70);

        var passage = Assert.Single(passages);
        Assert.Contains("Priya Shah", passage.Text);
        Assert.True(passage.Score > 0);
    }

    [Fact]
    public void Retriever_MatchesAmountsRegardlessOfThousandsSeparator()
    {
        var retriever = SmallPassageRetriever();
        retriever.Index(BuildDocument());

        var withSeparator = retriever.Retrieve("Which workstream has a $310,000 budget?", maxTokens: 70);
        var withoutSeparator = retriever.Retrieve("Which workstream has a 310000 budget?", maxTokens: 70);

        Assert.Contains("warehouse robotics", Assert.Single(withSeparator).Text);
        Assert.Equal(withSeparator.Single().Number, withoutSeparator.Single().Number);
    }

    [Fact]
    public void Retriever_ReturnsWholeDocumentWhenItFitsTheBudget()
    {
        var retriever = SmallPassageRetriever();
        retriever.Index(BuildDocument());

        var passages = retriever.Retrieve("anything at all", maxTokens: 10_000);

        Assert.Equal(retriever.PassageCount, passages.Count);
        Assert.Equal(Enumerable.Range(1, retriever.PassageCount), passages.Select(p => p.Number));
    }

    [Fact]
    public void Retriever_ReturnsNothingWhenNoTermMatches()
    {
        var retriever = SmallPassageRetriever();
        retriever.Index(BuildDocument());

        Assert.Empty(retriever.Retrieve("quantum entanglement", maxTokens: 70));
    }

    [Fact]
    public void Retriever_ReturnsNothing_BeforeADocumentIsIndexed_OrWithNoBudget()
    {
        var retriever = SmallPassageRetriever();
        Assert.Empty(retriever.Retrieve("Who leads the data migration?", maxTokens: 70));   // nothing indexed

        retriever.Index(BuildDocument());
        Assert.Empty(retriever.Retrieve("Who leads the data migration?", maxTokens: 0));
    }

    [Fact]
    public async Task ChatAgent_SendsOnlyRelevantPassagesWithCitationNumbers()
    {
        var client = new RecordingChatClient();
        var agent = new DocumentChatAgent(client, SmallPassageRetriever(), new ChatConfig { MaxPassageTokens = 70, MaxAnswerTokens = 321 });
        agent.InitializeSession("Plan.txt", BuildDocument(), "Ten workstreams are funded.");

        await agent.AskQuestionAsync("Who leads the security audit?");

        var call = client.Calls.Single();
        var prompt = call.Messages[^1].Text!;
        Assert.Contains("Marcus Lee", prompt);
        Assert.Matches(@"\[P\d+\] Section", prompt);
        Assert.DoesNotContain("Omar Haddad", prompt);
        Assert.Contains("Ten workstreams are funded.", call.Messages[0].Text);
        Assert.DoesNotContain("Omar Haddad", call.Messages[0].Text); // full document is no longer in the system prompt
        Assert.Equal(321, call.Options?.MaxOutputTokens);
    }

    [Fact]
    public async Task ChatAgent_UsesPreviousQuestionForTermlessFollowUps()
    {
        var client = new RecordingChatClient();
        var agent = new DocumentChatAgent(client, SmallPassageRetriever(), new ChatConfig { MaxPassageTokens = 70 });
        agent.InitializeSession("Plan.txt", BuildDocument(), string.Empty);

        await agent.AskQuestionAsync("Who leads the payroll upgrade?");
        await agent.AskQuestionAsync("And the budget?");

        Assert.Contains("$27,300", client.Calls[^1].Messages[^1].Text);
    }

    [Fact]
    public async Task ChatAgent_KeepsOnlyRecentTurnsWithoutOldPassages()
    {
        var client = new RecordingChatClient();
        var agent = new DocumentChatAgent(client, SmallPassageRetriever(), new ChatConfig { MaxPassageTokens = 70, MaxHistoryTurns = 2 });
        agent.InitializeSession("Plan.txt", BuildDocument(), string.Empty);

        foreach (var w in Workstreams.Take(5))
        {
            await agent.AskQuestionAsync($"Who leads the {w.Topic}?");
        }

        var last = client.Calls[^1].Messages;
        Assert.Equal(1 + 2 * 2 + 1, last.Count); // system + two retained turns + new question
        Assert.Equal("Who leads the vendor onboarding?", last[1].Text); // oldest retained turn is stored without passages
        Assert.Equal(1 + 2 * 2, agent.ChatHistory.Count);
    }

    [Fact]
    public async Task ChatAgent_RequiresASession()
    {
        var agent = new DocumentChatAgent(new RecordingChatClient());

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.AskQuestionAsync("Who leads it?"));
    }

    [Fact]
    public async Task ChatAgent_NewSummaryKeepsTheConversation()
    {
        var client = new RecordingChatClient();
        var agent = new DocumentChatAgent(client, SmallPassageRetriever(), new ChatConfig { MaxPassageTokens = 70 });
        agent.InitializeSession("Plan.txt", BuildDocument(), string.Empty);
        await agent.AskQuestionAsync("Who leads the security audit?");

        agent.UpdateSummary("NEW SUMMARY");
        await agent.AskQuestionAsync("Who leads the data migration?");

        var last = client.Calls[^1].Messages;
        Assert.Contains("NEW SUMMARY", last[0].Text);
        Assert.Equal("Who leads the security audit?", last[1].Text);
    }

    [Fact]
    public async Task ChatAgent_ClearingTheConversationKeepsTheDocument()
    {
        var client = new RecordingChatClient();
        var agent = new DocumentChatAgent(client, SmallPassageRetriever(), new ChatConfig { MaxPassageTokens = 70 });
        agent.InitializeSession("Plan.txt", BuildDocument(), string.Empty);
        await agent.AskQuestionAsync("Who leads the security audit?");

        agent.ClearConversation();
        await agent.AskQuestionAsync("Who leads the payroll upgrade?");

        Assert.True(agent.HasDocument);
        Assert.Equal(2, client.Calls[^1].Messages.Count); // system + new question only
        Assert.Contains("Aisha Khan", client.Calls[^1].Messages[^1].Text);
    }

    [Fact]
    public async Task ChatAgent_FailedAnswerIsNotAddedToHistory()
    {
        var agent = new DocumentChatAgent(new FailingChatClient(), SmallPassageRetriever());
        agent.InitializeSession("Plan.txt", BuildDocument(), string.Empty);

        await Assert.ThrowsAsync<LocalModelUnavailableException>(() => agent.AskQuestionAsync("Who leads the security audit?"));

        Assert.Single(agent.ChatHistory); // only the system prompt
    }

    [Fact]
    public async Task ChatAgent_TellsTheModelWhenNoPassageMatches()
    {
        var client = new RecordingChatClient();
        var agent = new DocumentChatAgent(client, SmallPassageRetriever(), new ChatConfig { MaxPassageTokens = 70 });
        agent.InitializeSession("Plan.txt", BuildDocument(), string.Empty);

        await agent.AskQuestionAsync("Quantum chromodynamics?");

        Assert.Contains("(No passage of the document matched this question.)", client.Calls.Single().Messages[^1].Text);
    }

    [Fact]
    public async Task ChatAgent_IgnoresBlankQuestions()
    {
        var client = new RecordingChatClient();
        var agent = new DocumentChatAgent(client, SmallPassageRetriever());
        agent.InitializeSession("Plan.txt", BuildDocument(), string.Empty);

        Assert.Equal(string.Empty, await agent.AskQuestionAsync("   "));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public void ChatAgent_RejectsASummaryBeforeADocument()
    {
        var agent = new DocumentChatAgent(new RecordingChatClient());

        Assert.Throws<InvalidOperationException>(() => agent.UpdateSummary("summary"));
    }

    [Fact]
    public async Task ChatAgent_ResetForgetsTheDocument()
    {
        var agent = new DocumentChatAgent(new RecordingChatClient(), SmallPassageRetriever());
        agent.InitializeSession("Plan.txt", BuildDocument(), "summary");

        agent.Reset();

        Assert.False(agent.HasDocument);
        Assert.Empty(agent.ChatHistory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.AskQuestionAsync("Who leads the security audit?"));
    }

    private sealed class FailingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new LocalModelUnavailableException("Foundry Local is not running.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((messages.ToList(), options));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ANSWER")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
