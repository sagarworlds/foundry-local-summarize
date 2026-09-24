using System.Net;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

/// <summary>
/// What the user sees when something goes wrong: every failure must end in a clear, actionable message rather than
/// an exception, a hang or a false "ready".
/// </summary>
public class FailurePathTests
{
    private const string Base = "127.0.0.1:56294";
    private const string ServerStatus = "State    Ready\nWeb URLs http://127.0.0.1:56294";
    private const string ModelList = """
        │ Model Name   │ Type   │ Cached │
        │ phi-4-mini   │ Chat   │ ●      │
        │ qwen3-0.6b   │ Chat   │ ○      │
        │ whisper-tiny │ Speech │ ●      │
        """;

    /// <summary>A server whose every answer is decided by the test; unknown hosts refuse the connection.</summary>
    private sealed class Server(Func<string, CancellationToken, Task<HttpResponseMessage>> respond, string authority = Base) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        public Server(Func<string, HttpResponseMessage> respond, string authority = Base) : this((p, _) => Task.FromResult(respond(p)), authority) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Authority != authority) throw new HttpRequestException($"Connection refused ({request.RequestUri.Authority})");
            Requests.Add(request.RequestUri.PathAndQuery);
            return respond(request.RequestUri.AbsolutePath, cancellationToken);
        }
    }

    /// <summary>A foundry CLI with scripted answers per command.</summary>
    private sealed class Cli(Func<string, FoundryCliResult> respond) : IFoundryCli
    {
        public List<string> Commands { get; } = new();

        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments);
            return Task.FromResult(respond(arguments));
        }

        public static Cli Running(string modelList = ModelList) => new(args => args switch
        {
            "server status" => new FoundryCliResult(true, ServerStatus, null),
            "model list" => new FoundryCliResult(true, modelList, null),
            _ => new FoundryCliResult(true, "", null)
        });
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };

    /// <summary>Foundry Local 1.x+ routes, with the load route overridable.</summary>
    private static Func<string, HttpResponseMessage> FoundryServer(Func<string, HttpResponseMessage>? load = null, Func<string, HttpResponseMessage>? loaded = null) => path => path switch
    {
        "/status" => Json("""{"modelCachePath":"C:\\cache","endpoints":["http://127.0.0.1:56294"]}"""),
        "/models/loaded" => loaded?.Invoke(path) ?? Json("[]"),
        _ when path.StartsWith("/models/load/") => load?.Invoke(path) ?? Json("""{"status":"loaded"}"""),
        _ => Json("""{"error":"not found"}""", HttpStatusCode.NotFound)
    };

    private static FoundryOptions Options(Action<LocalFoundryConfig>? configure = null)
    {
        var options = new FoundryOptions();
        configure?.Invoke(options.Local);
        return options;
    }

    private static async Task<string?> LoadProblem(Func<string, HttpResponseMessage> load, string model = "phi-4-mini", Action<LocalFoundryConfig>? configure = null)
    {
        using var service = new FoundryLocalService(Options(configure), Cli.Running(), new Server(FoundryServer(load)));
        service.SelectModel(model);
        var status = await service.CheckAsync(ensureModelLoaded: true);
        Assert.False(status.IsAvailable);
        return status.Problem;
    }

    // ---- Loading models (Foundry Local 1.x+) ----

    [Fact]
    public async Task Load_UnknownModel()
    {
        var problem = await LoadProblem(_ => Json("""{"error":"Model not found"}""", HttpStatusCode.NotFound));
        Assert.Contains("has no model named 'phi-4-mini'", problem);
    }

    [Fact]
    public async Task Load_ServerError_SuggestsRunningItInATerminal()
    {
        var problem = await LoadProblem(_ => Json("""{"error":"Load failed: out of GPU memory"}""", HttpStatusCode.InternalServerError));
        Assert.Contains("could not load 'phi-4-mini'", problem);
        Assert.Contains("out of GPU memory", problem);
        Assert.Contains("foundry model run phi-4-mini", problem);
    }

    [Fact]
    public async Task Load_ThatNeverFinishes_TimesOutWithAHint()
    {
        using var service = new FoundryLocalService(Options(o => o.ModelLoadTimeoutSeconds = 1), Cli.Running(),
            new Server(async (path, token) =>
            {
                if (!path.StartsWith("/models/load/")) return FoundryServer()(path);
                await Task.Delay(Timeout.Infinite, token);   // a load that never finishes; ended by the load timeout
                return Json("{}");
            }));
        service.SelectModel("phi-4-mini");

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.False(status.IsAvailable);
        Assert.Contains("did not answer within 1s", status.Problem);
        Assert.Contains("ModelLoadTimeoutSeconds", status.Problem);
    }

    [Theory]
    [InlineData(500, "[]")]          // server error
    [InlineData(200, "{not json")]   // unreadable answer
    public async Task LoadedListUnreadable_IsNotReady(int code, string body)
    {
        // /status identifies Foundry Local 1.x+, but its loaded-model list is broken (starting up, or faulty).
        var server = new Server(FoundryServer(loaded: _ => Json(body, (HttpStatusCode)code)));
        using var service = new FoundryLocalService(Options(), Cli.Running(), server);

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.False(status.IsAvailable);
        Assert.Contains("did not report which models are loaded", status.Problem);
    }

    [Fact]
    public async Task SwitchingModels_ReportsAnUnloadFailure()
    {
        var loaded = new List<string> { "qwen3-0.6b-generic-gpu:1" };
        var server = new Server(path => path switch
        {
            _ when path.StartsWith("/models/unload/") => Json("""{"error":"busy"}""", HttpStatusCode.InternalServerError),
            _ when path.StartsWith("/models/load/") => Add(loaded, "Phi-4-mini-instruct-generic-gpu:5"),
            "/models/loaded" => Json(System.Text.Json.JsonSerializer.Serialize(loaded)),
            _ => FoundryServer()(path)
        });
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, Cli.Running(), server));
        client.SelectModel("qwen3-0.6b");
        await client.LoadActiveModelAsync();

        var result = await client.SwitchModelAsync("phi-4-mini");

        Assert.True(result.Status.IsAvailable, result.Status.Problem);
        Assert.Contains("could not unload 'qwen3-0.6b'", result.UnloadProblem);

        static HttpResponseMessage Add(List<string> list, string id) { list.Add(id); return Json("""{"status":"loaded"}"""); }
    }

    // ---- Listing downloaded models from the CLI (Foundry Local 1.x+) ----

    [Fact]
    public async Task ModelList_FallsBackToCacheList_WhenTheCachedColumnIsUnreadable()
    {
        // e.g. the ●/○ marks garbled to "?" by a console code page
        var garbled = ModelList.Replace('●', '?').Replace('○', '?');
        var cli = new Cli(args => args switch
        {
            "server status" => new FoundryCliResult(true, ServerStatus, null),
            "model list" => new FoundryCliResult(true, garbled, null),
            "cache list" => new FoundryCliResult(true, "│ Alias │\n│ phi-4-mini │", null),
            _ => new FoundryCliResult(true, "", null)
        });
        using var service = new FoundryLocalService(Options(), cli, new Server(FoundryServer()));

        var list = await service.ListModelsAsync();

        Assert.Equal(new[] { "phi-4-mini" }, list.Models.Select(m => m.Id));
        Assert.Contains("cache list", cli.Commands);
    }

    [Fact]
    public async Task ModelList_OffersAllChatModels_WhenNothingSaysWhatIsDownloaded()
    {
        var garbled = ModelList.Replace('●', '?').Replace('○', '?');
        var cli = new Cli(args => args switch
        {
            "server status" => new FoundryCliResult(true, ServerStatus, null),
            "model list" => new FoundryCliResult(true, garbled, null),
            _ => new FoundryCliResult(true, "", null)   // cache list prints nothing usable
        });
        using var service = new FoundryLocalService(Options(), cli, new Server(FoundryServer()));

        var list = await service.ListModelsAsync();

        // Loading one that is not downloaded then fails with "not downloaded"; speech models stay hidden.
        Assert.Equal(new[] { "phi-4-mini", "qwen3-0.6b" }, list.Models.Select(m => m.Id));
    }

    [Fact]
    public async Task ModelList_ExplainsAnUnreadableCliOutput()
    {
        using var service = new FoundryLocalService(Options(), Cli.Running(modelList: "Error: catalog unavailable"), new Server(FoundryServer()));

        var list = await service.ListModelsAsync();

        Assert.Empty(list.Models);
        Assert.Contains("Could not read the model list from 'foundry model list'", list.Problem);
    }

    // ---- Finding and starting Foundry Local ----

    [Fact]
    public async Task StoppedService_WithAutoStartOff_SaysHowToStartIt()
    {
        var cli = new Cli(args => args == "server status" ? new FoundryCliResult(true, "State    Stopped", null) : new FoundryCliResult(true, "", null));
        using var service = new FoundryLocalService(Options(o => o.AutoStartService = false), cli, new Server(_ => Json("{}"), authority: "nowhere:1"));

        var status = await service.CheckAsync(ensureModelLoaded: false);

        Assert.False(status.IsAvailable);
        Assert.Contains("Foundry Local is not running. Start it with 'foundry server start'.", status.Problem);
        Assert.DoesNotContain("server start", cli.Commands);
    }

    [Fact]
    public async Task FailedStart_IsReported()
    {
        var cli = new Cli(args => args switch
        {
            "server status" => new FoundryCliResult(true, "State    Stopped", null),
            "server start" => new FoundryCliResult(false, "", "'foundry server start' exited with code 1: port in use"),
            _ => new FoundryCliResult(true, "", null)
        });
        using var service = new FoundryLocalService(Options(), cli, new Server(_ => Json("{}"), authority: "nowhere:1"));

        var status = await service.CheckAsync(ensureModelLoaded: false);

        Assert.Contains("Starting Foundry Local failed: 'foundry server start' exited with code 1: port in use", status.Problem);
    }

    [Fact]
    public async Task UnreachableService_IsReportedEverywhere()
    {
        using var service = new FoundryLocalService(Options(), new Cli(_ => new FoundryCliResult(false, "", "The 'foundry' command was not found.")),
            new Server(_ => Json("{}"), authority: "nowhere:1"));

        Assert.Contains("No local model service is reachable", (await service.ListModelsAsync()).Problem);
        Assert.Equal(ActiveModelStateKind.Unknown, (await service.GetActiveModelStateAsync()).Kind);
        Assert.Contains("No local model service is reachable", await service.ReloadActiveModelAsync());
        Assert.Null(await service.UnloadModelAsync("phi-4-mini"));   // nothing to unload
    }

    // ---- Generic OpenAI-compatible servers (e.g. Ollama) ----

    [Fact]
    public async Task OpenAICompatibleServer_ListsModels_AndNeedsNoLoading()
    {
        var server = new Server(path => path == "/v1/models"
            ? Json("""{"data":[{"id":"llama3.2:3b"},{"id":"nomic-embed-text"}]}""")
            : Json("{}", HttpStatusCode.NotFound), authority: "localhost:11434");
        using var service = new FoundryLocalService(Options(o => { o.AutoDiscover = false; o.Endpoint = "http://localhost:11434/v1"; o.ModelId = "llama3.2:3b"; o.AutoSelectModel = false; }),
            new Cli(_ => new FoundryCliResult(false, "", "not installed")), server);

        var list = await service.ListModelsAsync();

        Assert.Equal(new[] { "llama3.2:3b" }, list.Models.Select(m => m.Id));                  // embedding model hidden
        Assert.Equal(ActiveModelStateKind.Loaded, (await service.GetActiveModelStateAsync()).Kind);
        Assert.Null(await service.UnloadModelAsync("llama3.2:3b"));
    }

    [Fact]
    public async Task OpenAICompatibleServer_ExplainsAnUnreadableModelList()
    {
        var server = new Server(path => path == "/v1/models" ? Json("<html>proxy error</html>") : Json("{}", HttpStatusCode.NotFound), authority: "localhost:11434");
        using var service = new FoundryLocalService(Options(o => { o.AutoDiscover = false; o.Endpoint = "http://localhost:11434/v1"; }),
            new Cli(_ => new FoundryCliResult(false, "", "not installed")), server);

        var list = await service.ListModelsAsync();

        Assert.Contains("/v1/models returned unreadable JSON", list.Problem);
    }

    // ---- Chat requests ----

    private static FoundryLocalChatClient ChatClientFor(Func<string, HttpResponseMessage> chat, Func<string, HttpResponseMessage>? load = null)
    {
        var options = Options();
        var server = new Server(path => path == "/v1/chat/completions"
            ? chat(path)
            : FoundryServer(load, loaded: _ => Json("""["Phi-4-mini-instruct-generic-gpu:5"]"""))(path));
        var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, Cli.Running(), server));
        client.SelectModel("phi-4-mini");
        return client;
    }

    [Fact]
    public async Task Chat_UnknownModel()
    {
        using var client = ChatClientFor(_ => Json("""{"error":{"message":"Model not found"}}""", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Hi") }));

        Assert.Contains("does not know model 'phi-4-mini'", ex.Message);
    }

    [Fact]
    public async Task Chat_ModelThatCannotBeReloaded_ReportsTheLoadProblem()
    {
        using var client = ChatClientFor(
            chat: _ => Json("""{"error":{"message":"Model not loaded","type":"invalid_request_error"}}""", HttpStatusCode.BadRequest),
            load: _ => Json("""{"error":"Load failed"}""", HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Hi") }));

        Assert.Contains("could not load 'phi-4-mini'", ex.Message);
    }

    [Fact]
    public async Task Chat_Streaming_StreamsTheAnswer_AndFailsClearlyWithoutAModel()
    {
        using (var client = ChatClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK)
               {
                   Content = new StringContent(
                       "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
                       "data: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream")
               }))
        {
            var text = string.Concat(await client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "Hi") }).Select(u => u.Text).ToListAsync());
            Assert.Equal("Hello", text);
            Assert.Same(client, client.GetService(typeof(FoundryLocalChatClient)));
        }

        var options = Options();
        using var offline = new FoundryLocalChatClient(options, new FoundryLocalService(options, new Cli(_ => new FoundryCliResult(false, "", "not installed")),
            new Server(_ => Json("{}"), authority: "nowhere:1")));
        await Assert.ThrowsAsync<LocalModelUnavailableException>(async () =>
        {
            await foreach (var _ in offline.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "Hi") })) { }
        });
    }

    // ---- The real CLI wrapper and the legacy daemon file ----

    [Fact]
    public async Task FoundryCli_RunsARealProcess_AndReportsFailures()
    {
        // 'dotnet' is present wherever the tests run, so it stands in for the foundry executable.
        var dotnet = new FoundryCli("dotnet");

        var ok = await dotnet.RunAsync("--version", TimeSpan.FromSeconds(60));
        Assert.True(ok.Succeeded, ok.Problem);
        Assert.Matches(@"\d+\.\d+", ok.Output);

        var failed = await dotnet.RunAsync("no-such-command-xyz", TimeSpan.FromSeconds(60));
        Assert.False(failed.Succeeded);
        Assert.Contains("'dotnet no-such-command-xyz' exited with code", failed.Problem);

        var missing = await new FoundryCli("definitely-not-installed-cli-xyz").RunAsync("server status", TimeSpan.FromSeconds(10));
        Assert.False(missing.Succeeded);
        Assert.Contains("'definitely-not-installed-cli-xyz' command was not found", missing.Problem);

        Assert.Throws<ArgumentException>(() => new FoundryCli(" "));
    }

    [Theory]
    [InlineData("""{"web_urls":["http://127.0.0.1:5273/"]}""", "http://127.0.0.1:5273/v1")]
    [InlineData("""{"web_urls":[]}""", null)]
    [InlineData("""{"pid":1}""", null)]
    [InlineData("{not json", null)]
    public void DaemonFile_IsReadWhenValid_AndIgnoredOtherwise(string content, string? expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"daemon-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        try
        {
            Assert.Equal(expected, FoundryOptions.ReadDaemonFileEndpoint(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DaemonFile_Missing()
    {
        Assert.Null(FoundryOptions.ReadDaemonFileEndpoint(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));
    }
}
