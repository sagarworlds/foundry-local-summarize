using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Summarization;

namespace FoundrySummarizer.Tests;

/// <summary>
/// Output that stops at the model's output limit (finish reason "length") has lost everything after that point.
/// It must never pass silently: notes are re-read in smaller pieces, and summaries and answers are marked.
/// </summary>
public class CutOffTests
{
    /// <summary>A model whose answers (and whether they were cut off) the test decides, by request.</summary>
    private sealed class ScriptedModel(Func<IList<ChatMessage>, (string Text, bool CutOff)> respond) : IChatClient
    {
        public List<IList<ChatMessage>> Requests { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Requests.Add(list);
            var (text, cutOff) = respond(list);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                FinishReason = cutOff ? ChatFinishReason.Length : ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Requests.Add(list);
            var (text, cutOff) = respond(list);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            yield return new ChatResponseUpdate { FinishReason = cutOff ? ChatFinishReason.Length : ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Recorder<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) { lock (Reports) Reports.Add(value); }
    }

    private static readonly SummarizationConfig SmallBudget = new()
    {
        MaxSinglePassTokens = 300,
        MapChunkTokens = 150,
        MapChunkOverlapTokens = 0,
        MapMaxOutputTokens = 100,
        MaxCondenseRounds = 1
    };

    private static PromptyDocument Persona() => new PromptyEngine().GetPersona("Executive Bullets")!;

    private static bool IsNoteRequest(IList<ChatMessage> messages) => messages[0].Text == MultiPartSummarizer.NoteTakerSystemPrompt;

    /// <summary>The document text a note request was asked about.</summary>
    private static string PartOf(IList<ChatMessage> messages)
    {
        var prompt = messages[^1].Text!;
        int start = prompt.IndexOf('>', prompt.IndexOf("<document_part", StringComparison.Ordinal)) + 1;
        return prompt[start..prompt.IndexOf("</document_part>", StringComparison.Ordinal)].Trim();
    }

    private static string LongDocument(int sections) => string.Join("\n\n", Enumerable.Range(1, sections)
        .Select(i => $"Section {i}: Budget line {i} is ${i * 1000:N0} and is owned by Owner{i}. It is due on day {i} of the quarter."));

    // ---- Summaries ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASummaryThatStopsAtTheLimit_IsReportedAsCutOff(bool streamed)
    {
        var model = new ScriptedModel(_ => ("### 1. Executive Summary\n- The budget is", true));
        var summarizer = new MultiPartSummarizer(model, new PromptyEngine(), SmallBudget);

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(Persona(), "Budget is $150,000."),
            streamed ? new Recorder<SummarizationProgress>() : null);

        Assert.True(result.WasCutOff);
        Assert.Equal(0, result.CutOffNoteParts);
    }

    [Fact]
    public async Task ACompleteSummary_IsNotReportedAsCutOff()
    {
        var model = new ScriptedModel(_ => ("### 1. Executive Summary\n- The budget is $150,000.", false));
        var summarizer = new MultiPartSummarizer(model, new PromptyEngine(), SmallBudget);

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(Persona(), "Budget is $150,000."), new Recorder<SummarizationProgress>());

        Assert.False(result.WasCutOff);
    }

    [Fact]
    public async Task NotesThatStopAtTheLimit_AreRetakenInSmallerPieces_SoNoFactIsLost()
    {
        // Notes on a whole part hit the limit; notes on a piece half its size fit.
        var model = new ScriptedModel(messages =>
        {
            if (!IsNoteRequest(messages)) return ("FINAL", false);
            var part = PartOf(messages);
            return SemanticChunker.EstimateTokens(part) > 80 ? ("- Budget line 1 is", true) : ($"- notes on: {part[..20]}", false);
        });
        var summarizer = new MultiPartSummarizer(model, new PromptyEngine(), SmallBudget);
        var progress = new Recorder<SummarizationProgress>();

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(Persona(), LongDocument(20)), progress);

        Assert.Equal(0, result.CutOffNoteParts);                          // every part recovered
        Assert.False(result.WasCutOff);
        var final = model.Requests[^1][^1].Text!;
        Assert.DoesNotContain("- Budget line 1 is\n", final);             // the cut-off notes were replaced
        Assert.Contains("- notes on: Section 1:", final);
        Assert.Contains(progress.Reports, r => r.Status.Contains("reading it in") && r.Status.Contains("pieces"));

        // Every part was retried, and each piece was read once.
        var noteRequests = model.Requests.Where(IsNoteRequest).ToList();
        Assert.True(noteRequests.Count > 2 * result.PartCount - 1);
    }

    [Fact]
    public async Task NotesThatStillStopAtTheLimit_AreCounted()
    {
        var model = new ScriptedModel(messages => IsNoteRequest(messages) ? ("- Budget line", true) : ("FINAL", false));
        var summarizer = new MultiPartSummarizer(model, new PromptyEngine(), SmallBudget);

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(Persona(), LongDocument(20)));

        Assert.Equal(result.PartCount, result.CutOffNoteParts);
        Assert.Equal("FINAL", result.Summary);                            // the summary is still written from what was noted
    }

    [Fact]
    public async Task APartTooSmallToSplit_KeepsItsCutOffNotes()
    {
        // A part that is already a single small piece cannot be split further; its notes are kept and counted.
        var config = new SummarizationConfig { MaxSinglePassTokens = 100, MapChunkTokens = 50, MapChunkOverlapTokens = 0, MaxCondenseRounds = 1 };
        var model = new ScriptedModel(messages => IsNoteRequest(messages) ? ("- Budget line", true) : ("FINAL", false));
        var summarizer = new MultiPartSummarizer(model, new PromptyEngine(), config);
        var document = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => new string((char)('a' + i), 180)));

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(Persona(), document));

        Assert.Equal(result.PartCount, result.CutOffNoteParts);
        Assert.Equal(result.PartCount, model.Requests.Count(IsNoteRequest));  // no retries: nothing smaller to read
        Assert.Contains("- Budget line", model.Requests[^1][^1].Text);
    }

    // ---- Chat answers ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAnswerThatStopsAtTheLimit_IsReportedAsCutOff_AndKeptForContinue(bool streamed)
    {
        var model = new ScriptedModel(_ => ("The budget is $150,000 and the owners are", true));
        var agent = new DocumentChatAgent(model);
        agent.InitializeSession("minutes.txt", "Budget is $150,000.", summaryText: string.Empty);

        var answer = await agent.AskQuestionAsync("What is the budget?", streamed ? new Recorder<string>() : null);

        Assert.True(answer.WasCutOff);
        Assert.Equal("The budget is $150,000 and the owners are", agent.ChatHistory[^1].Text);   // "continue" can pick up from it
    }
}
