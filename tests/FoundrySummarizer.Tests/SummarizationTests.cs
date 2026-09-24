using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;

namespace FoundrySummarizer.Tests;

public class SummarizationTests
{
    private static readonly SummarizationConfig SmallBudget = new()
    {
        MaxSinglePassTokens = 300,
        MapChunkTokens = 150,
        MapChunkOverlapTokens = 0,
        MapMaxOutputTokens = 100,
        MaxCondenseRounds = 2
    };

    private static PromptyDocument ExecutivePersona() => new PromptyEngine().GetPersona("Executive Bullets")!;

    private static bool IsNoteRequest(IList<ChatMessage> messages) =>
        messages[0].Text == MultiPartSummarizer.NoteTakerSystemPrompt;

    private static string LongDocument(int paragraphs) => string.Join("\n\n", Enumerable.Range(1, paragraphs)
        .Select(i => $"Section {i}: Budget line {i} is ${i * 1000:N0} and is owned by Owner{i}. It is due on day {i} of the quarter."));

    [Fact]
    public async Task ShortDocument_IsSummarizedInOneCallWithPersonaOptions()
    {
        var client = new ScriptedChatClient((_, _) => "SUMMARY");
        var summarizer = new MultiPartSummarizer(client, new PromptyEngine(), SmallBudget);

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(ExecutivePersona(), "Budget is $150,000."));

        Assert.Equal("SUMMARY", result.Summary);
        Assert.False(result.UsedMultiPart);
        Assert.Equal(1, result.PartCount);
        var call = Assert.Single(client.Calls);
        Assert.Contains("Budget is $150,000.", call.Messages[^1].Text);
        Assert.Equal(0.1f, call.Options?.Temperature);
    }

    [Fact]
    public async Task LongDocument_IsReadInPartsAndSummarizedFromNotes()
    {
        int noteCalls = 0;
        var client = new ScriptedChatClient((messages, _) =>
            IsNoteRequest(messages) ? $"- note {++noteCalls}" : "FINAL");
        var summarizer = new MultiPartSummarizer(client, new PromptyEngine(), SmallBudget);

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(ExecutivePersona(), LongDocument(20)));

        Assert.Equal("FINAL", result.Summary);
        Assert.True(result.UsedMultiPart);
        Assert.True(result.PartCount > 1);
        Assert.Equal(result.PartCount, noteCalls);

        // Every part of the document is sent for note-taking exactly once, in order.
        var noteRequests = client.Calls.Where(c => IsNoteRequest(c.Messages)).ToList();
        Assert.Contains("Section 1:", noteRequests[0].Messages[1].Text);
        Assert.Contains("Section 20:", noteRequests[^1].Messages[1].Text);
        Assert.All(noteRequests, c => Assert.Equal(0f, c.Options?.Temperature));

        // The final persona call receives the notes, not the raw document.
        var finalPrompt = client.Calls[^1].Messages[^1].Text!;
        Assert.Contains("- note 1", finalPrompt);
        Assert.Contains($"[Part {result.PartCount} of {result.PartCount}]", finalPrompt);
        Assert.DoesNotContain("Section 20:", finalPrompt);
    }

    [Fact]
    public async Task PartsWithNothingRelevant_AreDroppedFromNotes()
    {
        var client = new ScriptedChatClient((messages, _) => IsNoteRequest(messages) ? "NONE" : "FINAL");
        var summarizer = new MultiPartSummarizer(client, new PromptyEngine(), SmallBudget);

        await summarizer.SummarizeAsync(new SummarizationRequest(ExecutivePersona(), LongDocument(20)));

        Assert.DoesNotContain("[Part 1 of", client.Calls[^1].Messages[^1].Text);
    }

    [Fact]
    public async Task ModelFailureDuringNoteTaking_StopsWithTheReason()
    {
        var client = new ScriptedChatClient((_, _) => throw new LocalModelUnavailableException("Foundry Local is not running."));
        var summarizer = new MultiPartSummarizer(client, new PromptyEngine(), SmallBudget);

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() =>
            summarizer.SummarizeAsync(new SummarizationRequest(ExecutivePersona(), LongDocument(20))));

        Assert.Equal("Foundry Local is not running.", ex.Message);
        Assert.Single(client.Calls); // no further parts are attempted
    }

    [Fact]
    public async Task EmptyDocument_IsRejected()
    {
        var summarizer = new MultiPartSummarizer(new ScriptedChatClient((_, _) => ""), new PromptyEngine(), SmallBudget);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            summarizer.SummarizeAsync(new SummarizationRequest(ExecutivePersona(), "   ")));
    }

    [Fact]
    public void Chunker_SplitsUnpunctuatedTranscriptLinesWithinBudget()
    {
        // Transcript lines separated by single newlines and without full stops used to form one huge chunk.
        var transcript = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"Speaker {i % 3}: we agreed item {i} goes to team {i % 5}"));
        var chunker = new SemanticChunker(maxTokensPerChunk: 100, overlapTokens: 0);

        var chunks = chunker.ChunkText(transcript, "t");

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 100, $"chunk {c.Index} has {c.TokenCount} tokens"));
        Assert.Contains("item 1 goes", chunks[0].Text);
        Assert.Contains("item 200 goes", chunks[^1].Text);
    }

    [Fact]
    public void Chunker_CarriesOnlyTrailingSentencesIntoNextChunk()
    {
        // Paragraph A is ~35 tokens and B ~19, so they cannot share a 50-token chunk; the last sentence
        // of A (~10 tokens) fits the 12-token overlap budget and should lead the second chunk.
        var text = "Alpha opens the plan for the regional rollout. Beta covers the vendor selection and budget review. Gamma closes with the approval deadline."
            + "\n\nDelta paragraph lists the follow-up owners and their dates for next month.";
        var chunker = new SemanticChunker(maxTokensPerChunk: 50, overlapTokens: 12);

        var chunks = chunker.ChunkText(text, "d");

        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("Gamma closes with the approval deadline.", chunks[1].Text);
        Assert.Contains("Delta paragraph", chunks[1].Text);
        Assert.DoesNotContain("Alpha", chunks[1].Text);
    }

    [Fact]
    public async Task NotesThatNeverShrinkEnough_StopAfterTheRoundLimit_AndAreStillSummarized()
    {
        // Every note is longer than the part it came from, so no number of rounds brings the notes under budget.
        var longNote = "- " + new string('n', 1000);
        int noteCalls = 0;
        var client = new ScriptedChatClient((messages, _) =>
        {
            if (!IsNoteRequest(messages)) return "FINAL";
            noteCalls++;
            return longNote;
        });
        var summarizer = new MultiPartSummarizer(client, new PromptyEngine(), SmallBudget);

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(ExecutivePersona(), LongDocument(20)));

        Assert.Equal("FINAL", result.Summary);
        Assert.True(noteCalls > result.PartCount);                         // a second (last) round ran on the notes
        var final = client.Calls[^1];
        Assert.False(IsNoteRequest(final.Messages));
        Assert.Contains("[Part 1 of", final.Messages[^1].Text);           // the final summary is written from the notes
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<IList<ChatMessage>, ChatOptions?, string> _respond;

        public ScriptedChatClient(Func<IList<ChatMessage>, ChatOptions?, string> respond) => _respond = respond;

        public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Calls.Add((list, options));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _respond(list, options))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
