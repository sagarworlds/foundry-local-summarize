using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;

namespace FoundrySummarizer.Tests;

/// <summary>
/// Summaries are sized to the model: a document that fits the model's context window is summarized in one call
/// instead of being read in parts, and a small window gets smaller parts so nothing is cut off.
/// </summary>
public class ContextBudgetTests : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("foundry-cache-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    /// <summary>Writes a genai_config.json the way ONNX Runtime GenAI models ship it.</summary>
    private void AddModel(string relativeFolder, int? contextLength, int? maxLength)
    {
        var folder = Path.Combine(_cache, relativeFolder);
        Directory.CreateDirectory(folder);
        var config = new Dictionary<string, object>();
        if (contextLength is not null) config["model"] = new { context_length = contextLength, type = "phi3" };
        if (maxLength is not null) config["search"] = new { max_length = maxLength, do_sample = false };
        File.WriteAllText(Path.Combine(folder, "genai_config.json"), JsonSerializer.Serialize(config));
    }

    // ---- Reading the window from the model's files ----

    [Fact]
    public void TheWindowIsTheSmallerOfTheModelsContextAndTheRuntimesLimit()
    {
        AddModel(Path.Combine("Microsoft", "Phi-4-mini-instruct-generic-gpu-5", "v5"), contextLength: 131072, maxLength: 16384);

        Assert.Equal(16384, ModelContextReader.FindContextTokens(_cache, "Phi-4-mini-instruct-generic-gpu:5"));
        Assert.Equal(16384, ModelContextReader.FindContextTokens(_cache, "phi-4-mini-instruct-generic-gpu"));   // any case, no version
    }

    [Theory]
    [InlineData(32768, null, 32768)]
    [InlineData(null, 4096, 4096)]
    [InlineData(null, null, null)]
    [InlineData(0, -1, null)]
    public void EitherLimitAloneIsUsed(int? contextLength, int? maxLength, int? expected)
    {
        AddModel(Path.Combine("Microsoft", "qwen2.5-7b-instruct-generic-gpu-3"), contextLength, maxLength);

        Assert.Equal(expected, ModelContextReader.FindContextTokens(_cache, "qwen2.5-7b-instruct-generic-gpu:3"));
    }

    [Fact]
    public void OnlyTheModelsOwnFolderCounts()
    {
        // "phi-4-mini" (an alias) must not pick up phi-4-mini-reasoning, nor the instruct model's longer name.
        AddModel(Path.Combine("Microsoft", "Phi-4-mini-reasoning-generic-gpu-1"), 4096, null);
        AddModel(Path.Combine("Microsoft", "Phi-4-mini-instruct-generic-gpu-5"), 16384, null);

        Assert.Null(ModelContextReader.FindContextTokens(_cache, "phi-4-mini"));
        Assert.Equal(16384, ModelContextReader.FindContextTokens(_cache, "Phi-4-mini-instruct-generic-gpu:5"));
        Assert.Null(ModelContextReader.FindContextTokens(_cache, "mistral-7b-instruct-generic-gpu:2"));
    }

    [Fact]
    public void WithSeveralVersions_TheRequestedOneIsUsed_OrElseTheSmallestWindow()
    {
        // "qwen2.5" contains a 5: versions are compared exactly, not by searching the folder name for the digit.
        AddModel(Path.Combine("Microsoft", "qwen2.5-7b-instruct-generic-gpu-3"), 32768, null);
        AddModel(Path.Combine("Microsoft", "qwen2.5-7b-instruct-generic-gpu-5"), 8192, null);

        Assert.Equal(32768, ModelContextReader.FindContextTokens(_cache, "qwen2.5-7b-instruct-generic-gpu:3"));
        Assert.Equal(8192, ModelContextReader.FindContextTokens(_cache, "qwen2.5-7b-instruct-generic-gpu:5"));
        Assert.Equal(8192, ModelContextReader.FindContextTokens(_cache, "qwen2.5-7b-instruct-generic-gpu"));
        Assert.Equal(8192, ModelContextReader.FindContextTokens(_cache, "qwen2.5-7b-instruct-generic-gpu:9"));   // not downloaded
    }

    [Fact]
    public void UnreadableOrMissingFiles_GiveNoWindow()
    {
        var folder = Path.Combine(_cache, "Microsoft", "Phi-4-mini-instruct-generic-gpu-5");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "genai_config.json"), "{not json");

        Assert.Null(ModelContextReader.FindContextTokens(_cache, "Phi-4-mini-instruct-generic-gpu:5"));
        Assert.Null(ModelContextReader.FindContextTokens(Path.Combine(_cache, "missing"), "Phi-4-mini-instruct-generic-gpu:5"));
        Assert.Null(ModelContextReader.FindContextTokens(null, "Phi-4-mini-instruct-generic-gpu:5"));
        Assert.Null(ModelContextReader.FindContextTokens(_cache, " "));
        Assert.Null(ModelContextReader.ReadContextTokens(Path.Combine(_cache, "missing.json")));
    }

    // ---- The service: setting first, then the model's files ----

    private const string CliStatus = "State    Ready\nWeb URLs http://127.0.0.1:56294";
    private const string CliModels = """
        │ Model Name   │ Type │ Cached │
        │ phi-4-mini   │ Chat │ ●      │
        """;

    private sealed class Cli : IFoundryCli
    {
        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FoundryCliResult(true, arguments == "model list" ? CliModels : CliStatus, null));
    }

    private sealed class Server(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public bool Up { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Up) throw new HttpRequestException("Connection refused");
            return Task.FromResult(respond(request.RequestUri!.AbsolutePath));
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };

    /// <summary>Foundry Local 1.x+ with phi-4-mini loaded, reporting <paramref name="cache"/> as its model cache.</summary>
    private static Server FoundryServer(string? cache) => new(path => path switch
    {
        "/status" => Json(cache is null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["modelCachePath"] = cache }),
        "/models/loaded" => Json(new[] { "Phi-4-mini-instruct-generic-gpu:5" }),
        _ when path.StartsWith("/models/load/") => Json(new { status = "loaded" }),
        _ => NotFound()
    });

    private static FoundryLocalService Service(Server server, Action<LocalFoundryConfig>? configure = null)
    {
        var options = new FoundryOptions();
        configure?.Invoke(options.Local);
        var service = new FoundryLocalService(options, new Cli(), server);
        service.SelectModel("phi-4-mini");
        return service;
    }

    [Fact]
    public async Task FoundryLocal_ReportsTheLoadedModelsWindow_FromItsFiles()
    {
        AddModel(Path.Combine("Microsoft", "Phi-4-mini-instruct-generic-gpu-5", "v5"), 131072, 16384);
        using var service = Service(FoundryServer(_cache));

        Assert.Equal(16384, await service.GetActiveContextTokensAsync());

        // Read once per model: the files are not searched again.
        Directory.Delete(Path.Combine(_cache, "Microsoft"), recursive: true);
        Assert.Equal(16384, await service.GetActiveContextTokensAsync());
    }

    [Fact]
    public async Task TheSetting_OverridesTheModelsFiles()
    {
        AddModel(Path.Combine("Microsoft", "Phi-4-mini-instruct-generic-gpu-5"), 131072, 16384);
        using var service = Service(FoundryServer(_cache), local => local.ContextWindows = new[] { new ModelContextWindow { Model = "PHI-4-Mini", Tokens = 8192 } });   // any case

        Assert.Equal(8192, await service.GetActiveContextTokensAsync());
    }

    [Fact]
    public async Task TheSetting_MatchesTheLoadedModelsFullId_AndIgnoresNonsense()
    {
        using var service = Service(FoundryServer(_cache), local => local.ContextWindows = new[]
        {
            new ModelContextWindow { Model = "qwen3-0.6b", Tokens = 0 },
            new ModelContextWindow { Model = "phi-4-mini-reasoning", Tokens = 4096 },
            new ModelContextWindow { Model = "Phi-4-mini-instruct-generic-gpu", Tokens = 12000 },
        });

        Assert.Equal(12000, await service.GetActiveContextTokensAsync());
    }

    [Fact]
    public async Task WithoutFilesOrASetting_TheWindowIsUnknown()
    {
        using var noCache = Service(FoundryServer(cache: null));
        Assert.Null(await noCache.GetActiveContextTokensAsync());

        using var emptyCache = Service(FoundryServer(_cache));
        Assert.Null(await emptyCache.GetActiveContextTokensAsync());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[\"not an object\"]")]
    [InlineData("{\"modelCachePath\":42}")]
    public async Task AnUnreadableStatus_GivesNoWindow(string status)
    {
        // Foundry Local 1.x+ is still recognized by its loaded-model list; only the cache folder is unknown.
        var server = new Server(path => path switch
        {
            "/status" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(status) },
            "/models/loaded" => Json(new[] { "Phi-4-mini-instruct-generic-gpu:5" }),
            _ => NotFound()
        });
        AddModel(Path.Combine("Microsoft", "Phi-4-mini-instruct-generic-gpu-5"), 16384, null);
        using var service = Service(server);

        Assert.Null(await service.GetActiveContextTokensAsync());
    }

    [Fact]
    public async Task AStoppedService_StillUsesTheSetting()
    {
        var server = FoundryServer(_cache);
        server.Up = false;

        using var configured = Service(server, local => local.ContextWindows = new[] { new ModelContextWindow { Model = "phi-4-mini", Tokens = 8192 } });
        Assert.Equal(8192, await configured.GetActiveContextTokensAsync());

        using var unconfigured = Service(server);
        Assert.Null(await unconfigured.GetActiveContextTokensAsync());
    }

    [Fact]
    public async Task FoundryLocal0x_ReportsItsModelFolderToo()
    {
        AddModel(Path.Combine("Microsoft", "Phi-4-mini-instruct-generic-gpu-5"), 16384, null);
        var server = new Server(path => path switch
        {
            "/openai/status" => Json(new { Endpoints = new[] { "http://127.0.0.1:5273" }, ModelDirPath = _cache }),
            "/openai/loadedmodels" => Json(new[] { "Phi-4-mini-instruct-generic-gpu:5" }),
            _ => NotFound()
        });
        var options = new FoundryOptions();
        using var service = new FoundryLocalService(options, new LegacyCli(), server);
        service.SelectModel("Phi-4-mini-instruct-generic-gpu:5");

        Assert.Equal(16384, await service.GetActiveContextTokensAsync());
    }

    private sealed class LegacyCli : IFoundryCli
    {
        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(arguments.StartsWith("server")
                ? new FoundryCliResult(false, "Unknown command 'server'.", "exit code 1")
                : new FoundryCliResult(true, "🟢 Model management service is running on http://127.0.0.1:5273/openai/status", null));
    }

    [Fact]
    public async Task AnOpenAICompatibleServer_HasNoFiles_ButTheSettingWorks()
    {
        var server = new Server(path => path == "/v1/models" ? Json(new { data = new[] { new { id = "llama3.2:3b" } } }) : NotFound());
        var options = new FoundryOptions();
        options.Local.AutoDiscover = false;
        options.Local.Endpoint = "http://127.0.0.1:56294/v1";
        options.Local.ModelId = "llama3.2:3b";
        options.Local.AutoSelectModel = false;
        using var plain = new FoundryLocalService(options, new Cli(), server);
        Assert.Null(await plain.GetActiveContextTokensAsync());

        options.Local.ContextWindows = new[] { new ModelContextWindow { Model = "llama3.2:3b", Tokens = 32768 } };
        using var configured = new FoundryLocalService(options, new Cli(), server);
        Assert.Equal(32768, await configured.GetActiveContextTokensAsync());
    }

    [Fact]
    public async Task TheChatClient_OffersTheWindowToAnyCaller()
    {
        AddModel(Path.Combine("Microsoft", "Phi-4-mini-instruct-generic-gpu-5"), 16384, null);
        var options = new FoundryOptions();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, new Cli(), FoundryServer(_cache)));
        client.SelectModel("phi-4-mini");

        var window = ((IChatClient)client).GetService<IModelContextWindow>();

        Assert.NotNull(window);
        Assert.Equal(16384, await window.GetContextTokensAsync());
    }

    // ---- Budgets ----

    [Theory]
    [InlineData(null, 2500)]                                   // unknown window: the configured value
    [InlineData(4096, 1676)]                                   // (4096 − 1500 − 500) × 0.8
    [InlineData(16384, 8000)]                                  // capped: small models lose details in very long prompts
    [InlineData(2048, SummaryBudget.MinimumSinglePassTokens)]  // too small for the style: the minimum
    public void TheSinglePassBudget_FitsTheWindow(int? window, int expected)
    {
        Assert.Equal(expected, SummaryBudget.SinglePassTokens(new SummarizationConfig(), window, summaryOutputTokens: 1500, promptTokens: 500));
    }

    [Fact]
    public void PartsAreAtMostHalfTheSinglePassBudget()
    {
        var config = new SummarizationConfig { MapChunkTokens = 1200 };

        Assert.Equal(1200, SummaryBudget.PartTokens(config, 8000));
        Assert.Equal(838, SummaryBudget.PartTokens(config, 1676));
        Assert.Equal(50, SummaryBudget.PartTokens(config, 60));
        Assert.Throws<ArgumentNullException>(() => SummaryBudget.PartTokens(null!, 100));
        Assert.Throws<ArgumentNullException>(() => SummaryBudget.SinglePassTokens(null!, 100, 1, 1));
    }

    /// <summary>A model client that knows its context window, as FoundryLocalChatClient does.</summary>
    private sealed class ModelWithWindow(int? window) : IChatClient, IModelContextWindow
    {
        public int Calls { get; private set; }

        public Task<int?> GetContextTokensAsync(CancellationToken cancellationToken = default) => Task.FromResult(window);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var isNote = messages.First().Text == MultiPartSummarizer.NoteTakerSystemPrompt;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, isNote ? "- note" : "FINAL")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }

    private static readonly PromptyDocument Persona = new PromptyEngine().GetPersona("Executive Bullets")!;

    // About 5,000 tokens: more than the 2,500 default, well within a 16K window.
    private static readonly string LongDocument = string.Join("\n\n", Enumerable.Range(1, 150)
        .Select(i => $"Section {i}: Budget line {i} is ${i * 1000:N0} and is owned by Owner{i}. It is due on day {i} of the quarter."));

    [Fact]
    public async Task ADocumentThatFitsTheWindow_IsSummarizedInOneCall()
    {
        var model = new ModelWithWindow(16384);
        var summarizer = new MultiPartSummarizer(model, new PromptyEngine());

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(Persona, LongDocument));

        Assert.False(result.UsedMultiPart);
        Assert.Equal(1, model.Calls);
        Assert.Equal(8000, await summarizer.GetSinglePassTokenLimitAsync(Persona));
    }

    [Theory]
    [InlineData(null)]                                          // window unknown: the configured 2,500
    [InlineData(4096)]                                          // small window: smaller parts
    public async Task ADocumentThatDoesNotFit_IsStillReadInParts(int? window)
    {
        var model = new ModelWithWindow(window);
        var summarizer = new MultiPartSummarizer(model, new PromptyEngine());

        var result = await summarizer.SummarizeAsync(new SummarizationRequest(Persona, LongDocument));

        Assert.True(result.UsedMultiPart);
        Assert.Equal(result.PartCount + 1, model.Calls);
        var limit = await summarizer.GetSinglePassTokenLimitAsync(Persona);
        Assert.True(window is null ? limit == 2500 : limit < 2500, $"limit {limit}");
        Assert.True(SemanticChunker.EstimateTokens(LongDocument) / result.PartCount <= SummaryBudget.PartTokens(new SummarizationConfig(), limit) + 50);
    }
}
