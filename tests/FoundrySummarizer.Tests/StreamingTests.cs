using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;

namespace FoundrySummarizer.Tests;

/// <summary>
/// Answers and summaries are shown while the model writes them. The streamed text must end up exactly as the
/// complete answer would be, with the model's reasoning never shown, and failures must still be explained.
/// </summary>
public class StreamingTests
{
    // ---- Reasoning is held back while streaming ----

    public static TheoryData<string, string> ReasoningOutputs => new()
    {
        { "qwen3-0.6b-generic-gpu:1", "<think>The budget is maybe $1m?</think>\n\n### Summary\n- Budget: $150,000" },
        { "qwen3-0.6b-generic-gpu:1", "<THINK>\nhmm\n</THINK>Answer" },
        { "qwen3-0.6b-generic-gpu:1", "<think>\n\n</think>\n\nAnswer" },                      // Qwen3 with /no_think
        { "qwen3-0.6b-generic-gpu:1", "Answer<think>second thoughts cut off by the output limit" },
        { "qwen3-0.6b-generic-gpu:1", "<think>only thinking, cut off" },
        { "Phi-4-mini-instruct-generic-gpu:5", "Plain answer with a < sign and </b> tag-like text." },
        { "Phi-4-mini-instruct-generic-gpu:5", "Ends with a lone <" },
        { "deepseek-r1-distill-qwen-7b-generic-gpu:3", "the user wants a summary...</think>\nAnswer" },   // template opened <think>
        { "deepseek-r1-distill-qwen-7b-generic-gpu:3", "An answer without any reasoning." },
    };

    [Theory]
    [MemberData(nameof(ReasoningOutputs))]
    public void StreamedAnswer_MatchesTheCompleteAnswer_HoweverTheOutputIsSplit(string modelId, string raw)
    {
        var expected = ReasoningOutputFilter.RemoveReasoning(raw, out var expectedHadReasoning);

        foreach (var size in new[] { 1, 2, 3, 5, 8, raw.Length })
        {
            var filter = new StreamingReasoningFilter(modelId);
            var shown = new StringBuilder();
            for (int i = 0; i < raw.Length; i += size)
            {
                shown.Append(filter.Push(raw.Substring(i, Math.Min(size, raw.Length - i))));
            }

            shown.Append(filter.Complete());

            Assert.Equal(expected, shown.ToString().TrimEnd());
            Assert.Equal(expectedHadReasoning, filter.HadReasoning);
            Assert.Equal(expected.Length > 0, filter.HasAnswer);
        }
    }

    [Fact]
    public void ReasoningIsNeverShown_EvenBeforeItIsClosed()
    {
        var filter = new StreamingReasoningFilter("qwen3-0.6b-generic-gpu:1");

        Assert.Equal(string.Empty, filter.Push("<thi"));                  // could become <think>: wait
        Assert.Equal(string.Empty, filter.Push("nk>The budget is maybe"));
        Assert.Equal(string.Empty, filter.Push(" $1m?</th"));
        Assert.Equal("The", filter.Push("ink>\n\nThe"));
        Assert.Equal(" budget", filter.Push(" budget"));
    }

    [Fact]
    public void DeepSeekReasoning_IsHeldUntilItEnds()
    {
        var filter = new StreamingReasoningFilter("deepseek-r1-distill-qwen-7b-generic-gpu:3");

        Assert.Equal(string.Empty, filter.Push("Okay, the user wants the budget. "));   // thoughts, not shown
        Assert.Equal("It is $150,000.", filter.Push("</think>\n\nIt is $150,000."));
    }

    [Fact]
    public void AStrayClosingTagInAPlainAnswer_IsDropped()
    {
        // Already-shown text cannot be taken back, so only the tag goes (the complete answer would drop the text before it).
        var filter = new StreamingReasoningFilter("Phi-4-mini-instruct-generic-gpu:5");

        var shown = filter.Push("Budget: $150,000.") + filter.Push("</think> Owner: Priya.") + filter.Complete();

        Assert.Equal("Budget: $150,000. Owner: Priya.", shown);
        Assert.True(filter.HadReasoning);
    }

    // ---- The Foundry Local client streams the filtered answer ----

    private const string ServerStatus = "State    Ready\nWeb URLs http://127.0.0.1:56294";
    private const string ModelList = """
        │ Model Name   │ Type │ Cached │
        │ phi-4-mini   │ Chat │ ●      │
        │ qwen3-0.6b   │ Chat │ ●      │
        """;

    private sealed class Cli : IFoundryCli
    {
        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FoundryCliResult(true, arguments == "model list" ? ModelList : ServerStatus, null));
    }

    /// <summary>A Foundry Local 1.x+ server whose chat answers the test scripts, as a stream or in one piece.</summary>
    private sealed class Server(Func<bool, HttpResponseMessage> chat, string loadedModel) : HttpMessageHandler
    {
        public List<bool> ChatRequests { get; } = new();   // true = streaming request
        public List<string> Loads { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/chat/completions")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var streaming = JsonDocument.Parse(body).RootElement.TryGetProperty("stream", out var s) && s.GetBoolean();
                ChatRequests.Add(streaming);
                return chat(streaming);
            }

            if (path.StartsWith("/models/load/")) Loads.Add(path);
            return path switch
            {
                "/status" => Json("""{"modelCachePath":"C:\\cache"}"""),
                "/models/loaded" => Json($"[\"{loadedModel}\"]"),
                _ when path.StartsWith("/models/load/") => Json("""{"status":"loaded"}"""),
                _ => Json("{}", HttpStatusCode.NotFound)
            };
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };

    /// <summary>A server-sent-events stream of chat completion chunks, as OpenAI-compatible servers send them.</summary>
    private static HttpResponseMessage EventStream(params string[] pieces)
    {
        var sse = new StringBuilder();
        for (int i = 0; i < pieces.Length; i++)
        {
            var chunk = new
            {
                id = "c1", @object = "chat.completion.chunk", created = 1, model = "m",
                choices = new[] { new { index = 0, delta = new { content = pieces[i] }, finish_reason = i == pieces.Length - 1 ? "stop" : null } }
            };
            sse.Append("data: ").Append(JsonSerializer.Serialize(chunk)).Append("\n\n");
        }

        sse.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage Completion(string text) => Json(JsonSerializer.Serialize(new
    {
        id = "c1", @object = "chat.completion", created = 1, model = "m",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } }
    }));

    private static FoundryLocalChatClient ClientFor(Server server, string model)
    {
        var options = new FoundryOptions();
        var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, new Cli(), server));
        client.SelectModel(model);
        return client;
    }

    private static async Task<(string Text, List<ChatResponseUpdate> Updates)> Collect(IChatClient client)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "What is the budget?") }))
        {
            updates.Add(update);
        }

        return (string.Concat(updates.Select(u => u.Text)), updates);
    }

    [Fact]
    public async Task Client_StreamsTheAnswer_WithoutTheReasoning()
    {
        var server = new Server(_ => EventStream("<think>", "maybe $1m?", "</think>\n\n", "The budget", " is $150,000."), "qwen3-0.6b-generic-gpu:1");
        using var client = ClientFor(server, "qwen3-0.6b");

        var (text, updates) = await Collect(client);

        Assert.Equal("The budget is $150,000.", text);
        Assert.Equal(ChatFinishReason.Stop, updates.Last(u => u.FinishReason is not null).FinishReason);
        Assert.Equal(new[] { true }, server.ChatRequests);
    }

    [Fact]
    public async Task Client_ExplainsAStreamThatIsOnlyReasoning()
    {
        var server = new Server(_ => EventStream("<think>", "still thinking when the limit was reached"), "qwen3-0.6b-generic-gpu:1");
        using var client = ClientFor(server, "qwen3-0.6b");

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() => Collect(client));

        Assert.Contains("spent its whole answer on reasoning", ex.Message);
    }

    [Fact]
    public async Task Client_FallsBackToOneRequest_WhenTheServerRejectsTheStream()
    {
        // A server that cannot stream: the answer still arrives, in one piece.
        var server = new Server(streaming => streaming
            ? Json("""{"error":{"message":"stream is not supported"}}""", HttpStatusCode.BadRequest)
            : Completion("The budget is $150,000."), "Phi-4-mini-instruct-generic-gpu:5");
        using var client = ClientFor(server, "phi-4-mini");

        var (text, _) = await Collect(client);

        Assert.Equal("The budget is $150,000.", text);
        Assert.Equal(new[] { true, false }, server.ChatRequests);
    }

    [Fact]
    public async Task Client_ReloadsAModelUnloadedBeforeTheStreamStarted()
    {
        // The model's idle time-to-live expired: the stream is rejected, the model is loaded again and the answer arrives.
        // The stream and the first one-piece request both find the model unloaded; after the reload it answers.
        int chats = 0;
        var server = new Server(_ => ++chats <= 2
            ? Json("""{"error":{"message":"Model not loaded","type":"invalid_request_error"}}""", HttpStatusCode.BadRequest)
            : Completion("The budget is $150,000."), "Phi-4-mini-instruct-generic-gpu:5");
        using var client = ClientFor(server, "phi-4-mini");

        var (text, _) = await Collect(client);

        Assert.Equal("The budget is $150,000.", text);
        Assert.Equal(new[] { true, false, false }, server.ChatRequests);
        Assert.Contains(server.Loads, l => l.EndsWith("/phi-4-mini"));
    }

    [Fact]
    public async Task Client_DeliversHeldBackText_WhenTheStreamEnds()
    {
        // DeepSeek-R1 output is held while it may be reasoning; an answer without </think> arrives at the end.
        var server = new Server(_ => EventStream("The budget", " is $150,000."), "deepseek-r1-distill-qwen-7b-generic-gpu:3");
        using var client = ClientFor(server, "deepseek-r1-distill-qwen-7b-generic-gpu:3");

        var (text, updates) = await Collect(client);

        Assert.Equal("The budget is $150,000.", text);
        Assert.Equal("The budget is $150,000.", updates[^1].Text);
    }

    [Fact]
    public async Task Client_ExplainsAStreamThatBreaksOffAfterTheFirstWords()
    {
        var server = new Server(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"The budget\"},\"finish_reason\":null}]}\n\n" +
                "data: {not json\n\n", Encoding.UTF8, "text/event-stream")
        }, "Phi-4-mini-instruct-generic-gpu:5");
        using var client = ClientFor(server, "phi-4-mini");
        var shown = new List<string>();

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "Budget?") })) shown.Add(update.Text);
        });

        Assert.Equal(new[] { "The budget" }, shown);                         // the first words had already arrived
        Assert.Contains("The request to model 'phi-4-mini' failed", ex.Message);
        Assert.Equal(new[] { true }, server.ChatRequests);                    // not re-sent: text was already shown
    }

    [Fact]
    public async Task Client_ExplainsAStreamThatBreaksOff()
    {
        var server = new Server(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {not json\n\n", Encoding.UTF8, "text/event-stream")
        }, "Phi-4-mini-instruct-generic-gpu:5");
        using var client = ClientFor(server, "phi-4-mini");

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() => Collect(client));

        Assert.Contains("The request to model 'phi-4-mini' failed", ex.Message);
    }

    // ---- Callers see the text grow ----

    /// <summary>Streams a fixed answer in pieces and records what it was asked.</summary>
    private sealed class PieceByPiece(params string[] pieces) : IChatClient
    {
        public int StreamingCalls { get; private set; }
        public int WholeCalls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            WholeCalls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "NOTES")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            for (int i = 0; i < pieces.Length; i++)
            {
                await Task.Delay(60, cancellationToken);                    // slower than the 50 ms report interval
                yield return new ChatResponseUpdate(ChatRole.Assistant, pieces[i]) { FinishReason = i == pieces.Length - 1 ? ChatFinishReason.Stop : null };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Records reports synchronously (Progress&lt;T&gt; would post them later).</summary>
    private sealed class Recorder<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public async Task StreamResponse_ReportsTheTextSoFar_AndReturnsTheWholeResponse()
    {
        var model = new PieceByPiece("The budget", " is", " $150,000.");
        var partial = new Recorder<string>();

        var response = await model.StreamResponseAsync(new[] { new ChatMessage(ChatRole.User, "Budget?") }, null, partial);

        Assert.Equal("The budget is $150,000.", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(new[] { "The budget", "The budget is", "The budget is $150,000." }, partial.Reports);

        // Without a receiver, nothing is streamed.
        await model.StreamResponseAsync(new[] { new ChatMessage(ChatRole.User, "Budget?") }, null, partialText: null);
        Assert.Equal(1, model.WholeCalls);
    }

    [Fact]
    public async Task ChatAgent_StreamsTheAnswer_AndRemembersTheWholeOfIt()
    {
        var model = new PieceByPiece("It is", " $150,000 [P1].");
        var agent = new DocumentChatAgent(model);
        agent.InitializeSession("minutes.txt", "Budget is $150,000.", summaryText: string.Empty);
        var partial = new Recorder<string>();

        var answer = await agent.AskQuestionAsync("What is the budget?", partial);

        Assert.Equal("It is $150,000 [P1].", answer);
        Assert.Equal("It is", partial.Reports[0]);
        Assert.Equal("It is $150,000 [P1].", agent.ChatHistory[^1].Text);  // history holds the complete answer
        Assert.Equal(1, model.StreamingCalls);
    }

    [Fact]
    public async Task Summarizer_StreamsTheFinalSummary_ButNotTheNotes()
    {
        var model = new PieceByPiece("- Budget:", " $150,000");
        var summarizer = new MultiPartSummarizer(model, new PromptyEngine(), new SummarizationConfig { MaxSinglePassTokens = 60, MapChunkTokens = 30 });
        var progress = new Recorder<SummarizationProgress>();
        var longText = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"Section {i}: budget line {i} is ${i},000 and is owned by Owner{i}."));

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(new PromptyEngine().GetPersona("Executive Bullets")!, longText), progress);

        Assert.Equal("- Budget: $150,000", result.Summary);
        Assert.True(model.WholeCalls > 1);                                 // notes are taken in one piece each
        Assert.Equal(1, model.StreamingCalls);                             // only the final summary is streamed
        var drafts = progress.Reports.Where(r => r.Draft.Length > 0).ToList();
        Assert.Equal(new[] { "- Budget:", "- Budget: $150,000" }, drafts.Select(d => d.Draft));
        Assert.All(drafts, d => Assert.StartsWith("Writing the final summary from notes on", d.Status));
        Assert.Contains(progress.Reports, r => r.Status.StartsWith("Reading part 1 of") && r.Draft.Length == 0);
    }
}
