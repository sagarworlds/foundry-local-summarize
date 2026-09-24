using System.Net;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

public class FoundryLocalServiceTests
{
    private const string RunningStatus = "🟢 Model management service is running on http://127.0.0.1:5273/openai/status";

    /// <summary>Returns scripted output per CLI command and records the commands run.</summary>
    private sealed class FakeCli : IFoundryCli
    {
        private readonly Func<string, int, FoundryCliResult> _respond;
        private readonly Dictionary<string, int> _counts = new();

        public FakeCli(Func<string, int, FoundryCliResult> respond) => _respond = respond;

        public List<string> Commands { get; } = new();

        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments);
            _counts[arguments] = _counts.GetValueOrDefault(arguments) + 1;
            return Task.FromResult(_respond(arguments, _counts[arguments]));
        }

        public static FakeCli Always(string statusOutput) => new((_, _) => new FoundryCliResult(true, statusOutput, null));

        public static FakeCli NotInstalled() => new((_, _) => new FoundryCliResult(false, string.Empty, "The 'foundry' command was not found."));
    }

    /// <summary>Routes requests to a handler; hosts not in <paramref name="liveAuthorities"/> refuse connections.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly HashSet<string> _liveAuthorities;
        private readonly Func<string, HttpResponseMessage> _respond;

        private readonly List<string>? _bodies;

        public FakeServer(IEnumerable<string> liveAuthorities, Func<string, HttpResponseMessage> respond, List<string>? bodies = null)
        {
            _liveAuthorities = liveAuthorities as HashSet<string> ?? liveAuthorities.ToHashSet(); // a passed set stays live: tests can move the service
            _respond = respond;
            _bodies = bodies;
        }

        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (!_liveAuthorities.Contains(uri.Authority))
            {
                throw new HttpRequestException($"Connection refused ({uri.Authority})");
            }

            Requests.Add(uri.PathAndQuery);
            if (request.Content is not null)
            {
                _bodies?.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return _respond(uri.PathAndQuery);
        }

        public static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) };

        public static HttpResponseMessage NotFound(string body = "") =>
            new(HttpStatusCode.NotFound) { Content = new StringContent(body) };
    }

    /// <summary>A Foundry Local 0.x CLI: "server" is an unknown command; "service status" reports stopped until started.</summary>
    private static FakeCli LegacyCliStoppedUntilStarted() => new((args, call) => args switch
    {
        "server status" or "server start" => new FoundryCliResult(false, "Unknown command 'server'.", "'foundry server status' exited with code 1"),
        "service status" when call == 1 => new FoundryCliResult(true, "🔴 Model management service is not running!", null),
        "service start" => new FoundryCliResult(true, "Service started.", null),
        _ => new FoundryCliResult(true, RunningStatus, null)
    });

    private static FoundryOptions Options(Action<LocalFoundryConfig>? configure = null)
    {
        var options = new FoundryOptions();
        options.Local.Endpoint = "http://127.0.0.1:63715/v1";
        options.Local.ModelId = "qwen2.5-0.5b-instruct-generic-cpu";
        configure?.Invoke(options.Local);
        return options;
    }

    [Fact]
    public async Task Discovers_PortFromServiceStatus_AndPicksLoadedPreferredModel()
    {
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path switch
        {
            "/openai/status" => FakeServer.Json("{}"),
            "/openai/loadedmodels" => FakeServer.Json("""["qwen2.5-0.5b-instruct-generic-cpu","Phi-4-mini-instruct-generic-gpu"]"""),
            _ => FakeServer.NotFound()
        });
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.True(status.IsAvailable, status.Problem);
        Assert.Equal(new Uri("http://127.0.0.1:5273/v1"), status.Endpoint);
        Assert.Equal("Phi-4-mini-instruct-generic-gpu", status.ModelId);
        Assert.NotNull(status.Client);
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("/openai/load/"));   // already loaded
    }

    [Fact]
    public async Task Starts_ServiceWhenStatusSaysItIsNotRunning()
    {
        var cli = LegacyCliStoppedUntilStarted();
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, _ => FakeServer.Json("[]"));
        using var service = new FoundryLocalService(Options(), cli, server);

        var status = await service.CheckAsync(ensureModelLoaded: false);

        Assert.True(status.IsAvailable, status.Problem);
        // The newer "server" command is tried first; the 0.x CLI rejects it, so "service" is used.
        Assert.Equal(new[] { "server status", "service status", "service start", "service status" }, cli.Commands.Take(4));
    }

    [Fact]
    public async Task Reports_MissingCliAndUnreachableEndpoint()
    {
        var server = new FakeServer(Array.Empty<string>(), _ => FakeServer.NotFound());
        using var service = new FoundryLocalService(Options(), FakeCli.NotInstalled(), server);

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.False(status.IsAvailable);
        Assert.Null(status.Client);
        Assert.Contains("No local model service is reachable at http://127.0.0.1:63715/", status.Problem);
        Assert.Contains("'foundry' command was not found", status.Problem);
    }

    [Fact]
    public async Task Loads_ModelBeforeUse_AndExplainsLoadFailure()
    {
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path switch
        {
            "/openai/status" => FakeServer.Json("{}"),
            "/openai/loadedmodels" => FakeServer.Json("[]"),
            "/openai/models" => FakeServer.Json("""["qwen2.5-0.5b-instruct-generic-cpu"]"""),
            _ when path.StartsWith("/openai/load/") => FakeServer.NotFound("model not found in cache"),
            _ => FakeServer.NotFound()
        });
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.False(status.IsAvailable);
        Assert.Contains(server.Requests, r => r.StartsWith("/openai/load/qwen2.5-0.5b-instruct-generic-cpu?timeout="));
        Assert.Contains("could not load model 'qwen2.5-0.5b-instruct-generic-cpu' (HTTP 404: model not found in cache)", status.Problem);
        Assert.Contains("foundry model download", status.Problem);
    }

    /// <summary>A Foundry Local fake that tracks which models are in memory; <c>unload</c> simulates the idle time-to-live.</summary>
    private sealed class StatefulFoundry
    {
        public HashSet<string> Loaded { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int LoadCalls { get; private set; }
        public int ChatCalls { get; private set; }
        public Action? OnChat { get; set; }

        public FakeServer Server => new(new[] { "127.0.0.1:5273" }, path =>
        {
            if (path == "/openai/loadedmodels") return FakeServer.Json(System.Text.Json.JsonSerializer.Serialize(Loaded));
            if (path == "/openai/models") return FakeServer.Json("""["Phi-4-mini-instruct-generic-gpu:5"]""");
            if (path.StartsWith("/openai/load/"))
            {
                LoadCalls++;
                Loaded.Add(Uri.UnescapeDataString(path["/openai/load/".Length..].Split('?')[0]));
                return FakeServer.Json("{}");
            }
            if (path == "/v1/chat/completions")
            {
                ChatCalls++;
                OnChat?.Invoke();
                return Loaded.Count > 0
                    ? FakeServer.Json(CompletionJson)
                    : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(
                        """{"error":{"message":"Failed to handle OpenAI completion: Model 'Phi-4-mini-instruct-generic-gpu' is not loaded. Please load the model before getting a ChatClient.","type":"invalid_request_error","code":null}}""") };
            }
            return FakeServer.Json("{}");
        });
    }

    [Fact]
    public async Task Loads_ModelOnlyWhenTheServiceSaysItIsNotLoaded()
    {
        var foundry = new StatefulFoundry();
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), foundry.Server);

        Assert.True((await service.CheckAsync(ensureModelLoaded: true)).IsAvailable);
        Assert.True((await service.CheckAsync(ensureModelLoaded: true)).IsAvailable);
        Assert.Equal(1, foundry.LoadCalls);

        foundry.Loaded.Clear(); // Foundry Local unloaded it after its idle time-to-live
        Assert.True((await service.CheckAsync(ensureModelLoaded: true)).IsAvailable);
        Assert.Equal(2, foundry.LoadCalls);
    }

    [Fact]
    public async Task ChatClient_ReloadsAndRetriesWhenTheModelWasUnloaded()
    {
        var foundry = new StatefulFoundry();
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), foundry.Server));
        // Unloaded between the pre-request check and the request itself.
        foundry.OnChat = () => { if (foundry.ChatCalls == 1) foundry.Loaded.Clear(); };

        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize.") });

        Assert.Equal("SUMMARY", response.Text);
        Assert.Equal(2, foundry.ChatCalls);
        Assert.Equal(2, foundry.LoadCalls);
    }

    [Fact]
    public async Task ChatClient_ExplainsAModelThatWillNotStayLoaded()
    {
        var foundry = new StatefulFoundry();
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), foundry.Server));
        foundry.OnChat = () => foundry.Loaded.Clear();

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize.") }));

        Assert.Contains("could not keep model 'Phi-4-mini-instruct-generic-gpu:5' loaded", ex.Message);
        Assert.DoesNotContain("context", ex.Message);
    }

    [Fact]
    public async Task SwitchingModels_UnloadsThePreviousOneBeforeLoadingTheNewOne()
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qwen2.5-0.5b-instruct-generic-cpu" };
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path =>
        {
            var model = Uri.UnescapeDataString(path.Split('/').Last().Split('?')[0]);
            if (path == "/openai/loadedmodels") return FakeServer.Json(System.Text.Json.JsonSerializer.Serialize(loaded));
            if (path == "/openai/models") return FakeServer.Json("""["qwen2.5-0.5b-instruct-generic-cpu","Phi-4-mini-instruct-generic-gpu:5"]""");
            if (path.StartsWith("/openai/unload/")) { loaded.Remove(model); return FakeServer.Json("{}"); }
            if (path.StartsWith("/openai/load/")) { loaded.Add(model); return FakeServer.Json("{}"); }
            return FakeServer.Json("{}");
        });
        var options = Options();
        options.Local.AutoSelectModel = false; // start on the configured qwen model
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), server));
        Assert.True((await client.LoadActiveModelAsync()).IsAvailable);

        var result = await client.SwitchModelAsync("Phi-4-mini-instruct-generic-gpu:5");

        Assert.True(result.Status.IsAvailable, result.Status.Problem);
        Assert.Null(result.UnloadProblem);
        Assert.Equal("Phi-4-mini-instruct-generic-gpu:5", result.Status.ModelId);
        Assert.Equal(new[] { "Phi-4-mini-instruct-generic-gpu:5" }, loaded);

        var unload = server.Requests.FindIndex(r => r.StartsWith("/openai/unload/qwen2.5-0.5b-instruct-generic-cpu"));
        var load = server.Requests.FindIndex(r => r.StartsWith("/openai/load/Phi-4-mini"));
        Assert.InRange(unload, 0, load - 1); // unloaded first, so both never compete for GPU memory
    }

    [Fact]
    public async Task SwitchingModels_ReportsAFailedUnloadButStillLoadsTheNewModel()
    {
        var loaded = new List<string>();
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path switch
        {
            _ when path.StartsWith("/openai/unload/") => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ when path.StartsWith("/openai/load/") => Loaded(loaded, path),
            "/openai/loadedmodels" => FakeServer.Json(System.Text.Json.JsonSerializer.Serialize(loaded)),
            _ => FakeServer.Json("{}")
        });
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), server));
        await client.GetStatusAsync();

        var result = await client.SwitchModelAsync("Phi-4-mini-instruct-generic-gpu:5");

        Assert.True(result.Status.IsAvailable, result.Status.Problem);
        Assert.Contains("could not unload 'qwen2.5-0.5b-instruct-generic-cpu' (HTTP 500)", result.UnloadProblem);
    }

    [Fact]
    public async Task Unload_TreatsServersWithoutTheRouteAsSuccess()
    {
        var server = new FakeServer(new[] { "localhost:11434" }, _ => FakeServer.NotFound());
        var options = Options(o => { o.AutoDiscover = false; o.Endpoint = "http://localhost:11434/v1"; });
        using var service = new FoundryLocalService(options, FakeCli.NotInstalled(), server);
        await service.CheckAsync(ensureModelLoaded: false);

        Assert.Null(await service.UnloadModelAsync("llama3.2:3b"));
    }

    private const string CatalogJson = """
        [
          {"name":"Phi-4-mini-instruct-cuda-gpu:4","alias":"phi-4-mini","runtime":{"deviceType":"GPU","executionProvider":"CUDAExecutionProvider"}},
          {"name":"Phi-4-mini-instruct-generic-gpu:4","alias":"phi-4-mini","runtime":{"deviceType":"GPU","executionProvider":"WebGpuExecutionProvider"}},
          {"name":"Phi-4-mini-instruct-generic-gpu:5","alias":"phi-4-mini","runtime":{"deviceType":"GPU","executionProvider":"WebGpuExecutionProvider"}},
          {"name":"qwen3-0.6b-generic-gpu:1","alias":"qwen3-0.6b","runtime":{"deviceType":"GPU","executionProvider":"WebGpuExecutionProvider"}},
          {"name":"qwen2.5-0.5b-instruct-generic-cpu:3","alias":"qwen2.5-0.5b","runtime":{"deviceType":"CPU","executionProvider":"CPUExecutionProvider"}}
        ]
        """;

    /// <summary>
    /// The reported machine: /openai/models lists ids without their version, and /openai/load answers 404 unless it
    /// gets the exact catalog id, e.g. "Phi-4-mini-instruct-generic-gpu:5".
    /// </summary>
    private static FakeServer FoundryWithCatalog(HashSet<string> loaded, List<string>? chatBodies = null) =>
        new(new[] { "127.0.0.1:5273" }, path =>
        {
            var route = path.Split('?')[0];
            var model = Uri.UnescapeDataString(route.Split('/').Last());
            if (route == "/foundry/list") return FakeServer.Json(CatalogJson);
            if (route == "/openai/models") return FakeServer.Json("""["Phi-4-mini-instruct-generic-gpu","qwen3-0.6b-generic-gpu"]""");
            if (route == "/openai/loadedmodels") return FakeServer.Json(System.Text.Json.JsonSerializer.Serialize(loaded));
            if (route.StartsWith("/openai/load/"))
            {
                if (!model.Contains(':')) return FakeServer.NotFound();
                loaded.Add(model);
                return FakeServer.Json("{}");
            }
            if (route.StartsWith("/openai/unload/")) { loaded.Remove(model); return FakeServer.Json("{}"); }
            if (route == "/v1/chat/completions") return FakeServer.Json(CompletionJson);
            return FakeServer.Json("{}");
        }, chatBodies);

    [Fact]
    public void Catalog_ResolvesLikeTheOfficialSdk()
    {
        var catalog = FoundryCatalog.Parse(CatalogJson);

        Assert.Equal("Phi-4-mini-instruct-generic-gpu:5", catalog.Resolve("Phi-4-mini-instruct-generic-gpu")?.Id);   // newest version
        Assert.Equal("Phi-4-mini-instruct-generic-gpu:4", catalog.Resolve("phi-4-mini-instruct-generic-gpu:4")?.Id);  // exact
        Assert.Equal("Phi-4-mini-instruct-cuda-gpu:4", catalog.Resolve("phi-4-mini")?.Id);                            // alias, best device first
        Assert.Null(catalog.Resolve("no-such-model"));
        Assert.Equal("cuda", catalog.ExecutionProviderOverride(catalog.Resolve("Phi-4-mini-instruct-generic-gpu")!));
        Assert.Null(catalog.ExecutionProviderOverride(catalog.Resolve("qwen2.5-0.5b-instruct-generic-cpu")!));
    }

    [Fact]
    public async Task Loads_TheCatalogIdEvenWhenTheModelListOmitsTheVersion()
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bodies = new List<string>();
        var server = FoundryWithCatalog(loaded, bodies);
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), server));

        var list = await client.ListModelsAsync();
        Assert.Contains(list.Models, m => m.Id == "Phi-4-mini-instruct-generic-gpu:5");

        var result = await client.SwitchModelAsync("Phi-4-mini-instruct-generic-gpu");
        Assert.True(result.Status.IsAvailable, result.Status.Problem);
        Assert.Contains(server.Requests, r => r.StartsWith("/openai/load/Phi-4-mini-instruct-generic-gpu:5?timeout=") && r.EndsWith("&ep=cuda"));

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize.") });
        Assert.Contains("\"model\":\"Phi-4-mini-instruct-generic-gpu:5\"", Assert.Single(bodies));
    }

    [Fact]
    public async Task Load_ExplainsANameTheCatalogDoesNotKnow()
    {
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus),
            FoundryWithCatalog(new HashSet<string>())));

        var result = await client.SwitchModelAsync("no-such-model");

        Assert.False(result.Status.IsAvailable);
        Assert.Contains("catalog has no model named 'no-such-model'", result.Status.Problem);
    }

    [Fact]
    public async Task NotReady_WhenTheLoadIsAcceptedButTheModelNeverAppearsAsLoaded()
    {
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path switch
        {
            "/openai/loadedmodels" => FakeServer.Json("[]"),
            _ => FakeServer.Json("{}") // the load route answers 200 but nothing gets loaded
        });
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.False(status.IsAvailable);
        Assert.Contains("accepted the request to load 'qwen2.5-0.5b-instruct-generic-cpu', but it is not among the loaded models", status.Problem);
        Assert.Contains("reports no loaded models", status.Problem);
    }

    [Fact]
    public async Task NotReady_WhenFoundryLocalCannotReportItsLoadedModels()
    {
        // The service and its catalog answer, but the loaded-model list fails: "the server is up" is not enough.
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path switch
        {
            "/foundry/list" => FakeServer.Json(CatalogJson),
            "/openai/loadedmodels" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => FakeServer.Json("{}")
        });
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.False(status.IsAvailable);
        Assert.Contains("did not report which models are loaded", status.Problem);
    }

    [Fact]
    public async Task ActiveModelState_ReflectsTheServiceNow()
    {
        var foundry = new StatefulFoundry();
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), foundry.Server);
        Assert.Equal(ActiveModelStateKind.NotLoaded, (await service.GetActiveModelStateAsync()).Kind);

        await service.CheckAsync(ensureModelLoaded: true);
        Assert.Equal(ActiveModelStateKind.Loaded, (await service.GetActiveModelStateAsync()).Kind);

        foundry.Loaded.Clear();                                          // unloaded after its idle time-to-live
        Assert.Equal(ActiveModelStateKind.NotLoaded, (await service.GetActiveModelStateAsync()).Kind);
    }

    [Theory]
    [InlineData("Phi-4-mini-instruct-generic-gpu:5")]   // exact id
    [InlineData("Phi-4-mini-instruct-generic-gpu")]     // id without its version
    [InlineData("phi-4-mini")]                          // alias
    public async Task ActiveModelState_RecognisesEverySpellingOfTheLoadedModel(string reportedName)
    {
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path.Split('?')[0] switch
        {
            "/foundry/list" => FakeServer.Json(CatalogJson),
            "/openai/loadedmodels" => FakeServer.Json($"[\"{reportedName}\"]"),
            _ => FakeServer.Json("{}")
        });
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);
        service.SelectModel("Phi-4-mini-instruct-generic-gpu:5");

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.True(status.IsAvailable, status.Problem);
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("/openai/load/"));    // already loaded: no reload
        Assert.Equal(ActiveModelStateKind.Loaded, (await service.GetActiveModelStateAsync()).Kind);
    }

    [Fact]
    public async Task ActiveModelState_SaysWhichModelIsLoadedWhenItIsAnotherOne()
    {
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path.Split('?')[0] switch
        {
            "/foundry/list" => FakeServer.Json(CatalogJson),
            "/openai/loadedmodels" => FakeServer.Json("""["Phi-4-mini-instruct-cuda-gpu:4"]"""),
            _ => FakeServer.Json("{}")
        });
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);
        service.SelectModel("Phi-4-mini-instruct-generic-gpu:5");
        await service.CheckAsync(ensureModelLoaded: false);

        var state = await service.GetActiveModelStateAsync();

        Assert.Equal(ActiveModelStateKind.NotLoaded, state.Kind);
        Assert.Contains("reports loaded: Phi-4-mini-instruct-cuda-gpu:4; the app is set to 'Phi-4-mini-instruct-generic-gpu:5'", state.Problem);
    }

    [Fact]
    public async Task ActiveModelState_FollowsTheServiceToANewPort()
    {
        // The model was loaded at :5273; the service then restarted on :6001 (e.g. after 'foundry service restart').
        int statusCalls = 0;
        var cli = new FakeCli((_, _) => new FoundryCliResult(true,
            ++statusCalls == 1 ? RunningStatus : RunningStatus.Replace("5273", "6001"), null));
        var live = new HashSet<string> { "127.0.0.1:5273" };
        var server = new FakeServer(live, path => path == "/openai/loadedmodels"
            ? FakeServer.Json("""["qwen2.5-0.5b-instruct-generic-cpu"]""")
            : FakeServer.Json("{}"));
        using var service = new FoundryLocalService(Options(), cli, server);
        Assert.True((await service.CheckAsync(ensureModelLoaded: true)).IsAvailable);

        live.Clear();
        live.Add("127.0.0.1:6001");

        Assert.Equal(ActiveModelStateKind.Loaded, (await service.GetActiveModelStateAsync()).Kind);
    }

    [Fact]
    public async Task ActiveModelState_RetriesOnceAndThenSaysWhatFailed()
    {
        int listings = 0;
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path == "/openai/loadedmodels"
            ? (++listings == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : FakeServer.Json("""["qwen2.5-0.5b-instruct-generic-cpu"]"""))
            : FakeServer.Json("{}"));
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        Assert.Equal(ActiveModelStateKind.Loaded, (await service.GetActiveModelStateAsync()).Kind); // one hiccup is retried

        var failing = new FakeServer(new[] { "127.0.0.1:5273" }, path => path == "/openai/loadedmodels"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : FakeServer.Json("{}"));
        using var broken = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), failing);
        var state = await broken.GetActiveModelStateAsync();

        Assert.Equal(ActiveModelStateKind.Unknown, state.Kind);
        Assert.Contains("could not list its loaded models", state.Problem);
        Assert.Contains("HTTP 500", state.Problem);
    }

    [Theory]
    [InlineData("Phi-4-mini-instruct-generic-gpu:5", "Phi-4-mini-instruct-generic-gpu", true)]
    [InlineData("phi-4-mini-instruct-generic-gpu", "Phi-4-mini-instruct-generic-gpu:12", true)]
    [InlineData("llama3.2:3b", "llama3.2", false)]
    [InlineData("llama3.2:3b", "llama3.2:1b", false)]
    [InlineData("qwen2.5-0.5b-instruct-generic-cpu", "Phi-4-mini-instruct-generic-gpu", false)]
    public void SameModel_IgnoresOnlyNumericVersionSuffixes(string a, string b, bool expected)
    {
        Assert.Equal(expected, FoundryLocalService.SameModel(a, b));
    }

    [Fact]
    public async Task Rediscovers_WhenServiceMovesToANewPort()
    {
        int statusCall = 0;
        var cli = new FakeCli((_, _) => new FoundryCliResult(true,
            ++statusCall == 1 ? RunningStatus : RunningStatus.Replace("5273", "6001"), null));
        var server = new FakeServer(new[] { "127.0.0.1:6001" }, _ => FakeServer.Json("[]"));
        using var service = new FoundryLocalService(Options(), cli, server);

        var status = await service.CheckAsync(ensureModelLoaded: false);

        Assert.True(status.IsAvailable, status.Problem);
        Assert.Equal(new Uri("http://127.0.0.1:6001/v1"), status.Endpoint);
    }

    [Fact]
    public async Task Works_WithOllamaStyleServerWithoutFoundryRoutes()
    {
        var server = new FakeServer(new[] { "localhost:11434" }, path => path == "/v1/models"
            ? FakeServer.Json("""{"data":[{"id":"llama3.2:3b"}]}""")
            : FakeServer.NotFound());
        var cli = FakeCli.NotInstalled();
        using var service = new FoundryLocalService(Options(o =>
        {
            o.AutoDiscover = false;
            o.Endpoint = "http://localhost:11434/v1";
            o.ModelId = "llama3.2:3b";
        }), cli, server);

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.True(status.IsAvailable, status.Problem);
        Assert.Equal("llama3.2:3b", status.ModelId);
        Assert.Empty(cli.Commands);                                          // discovery off
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("/openai/load/"));
    }

    [Theory]
    [InlineData(RunningStatus, "http://127.0.0.1:5273/")]
    [InlineData("Model management service is running on http://localhost:61234/openai/status\n", "http://localhost:61234/")]
    [InlineData("🔴 Model management service is not running!", null)]
    [InlineData("", null)]
    public void ParseServiceUri_ReadsAddressFromStatusOutput(string output, string? expected)
    {
        Assert.Equal(expected, FoundryCli.ParseServiceUri(output)?.ToString());
    }

    [Fact]
    public async Task ChatClient_ThrowsWithTheReasonWhenNoModelIsReachable()
    {
        var server = new FakeServer(Array.Empty<string>(), _ => FakeServer.NotFound());
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.NotInstalled(), server));

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize: budget is $150,000.") }));

        Assert.Contains("No local model service is reachable at http://127.0.0.1:63715/", ex.Message);
        Assert.Contains("'foundry' command was not found", ex.Message);
    }

    /// <summary>A Foundry Local fake with phi-4-mini loaded, whose chat endpoint answers with <paramref name="chat"/>.</summary>
    private static FakeServer FoundryWithChat(Func<string, HttpResponseMessage> chat, List<string>? chatBodies = null) =>
        new(new[] { "127.0.0.1:5273" }, path => path switch
        {
            "/openai/loadedmodels" => FakeServer.Json("""["phi-4-mini-instruct-generic-gpu"]"""),
            "/v1/chat/completions" => chat(path),
            _ => FakeServer.Json("{}")
        }, chatBodies);

    private const string CompletionJson = """
        {"id":"c1","object":"chat.completion","created":1,"model":"phi-4-mini-instruct-generic-gpu",
         "choices":[{"index":0,"message":{"role":"assistant","content":"SUMMARY"},"finish_reason":"stop"}]}
        """;

    [Fact]
    public async Task ChatClient_SendsMaxTokensThatFoundryLocalAccepts()
    {
        // Foundry Local answers HTTP 400 to "max_completion_tokens", which the OpenAI SDK sends by default.
        var bodies = new List<string>();
        var server = FoundryWithChat(_ => FakeServer.Json(CompletionJson), bodies);
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), server));

        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize.") }, new ChatOptions { MaxOutputTokens = 1500 });

        Assert.Equal("SUMMARY", response.Text);
        var body = Assert.Single(bodies);
        Assert.Contains("\"max_tokens\":1500", body);
        Assert.DoesNotContain("max_completion_tokens", body);
    }

    [Fact]
    public async Task ChatClient_ExplainsATextTooLongForTheModel()
    {
        var server = FoundryWithChat(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"type":"invalid_request_error","message":""},"detail":"prompt exceeds max length"}""")
        });
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), server));

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }));

        Assert.Contains("The text is too long for model", ex.Message);
        Assert.Contains("prompt exceeds max length", ex.Message);
        Assert.Contains("MaxSinglePassTokens", ex.Message);
    }

    [Fact]
    public async Task ChatClient_ShowsOtherRejectionsWithoutGuessingTheCause()
    {
        var server = FoundryWithChat(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"type":"invalid_request_error","message":"unsupported parameter: foo"}}""")
        });
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), server));

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }));

        Assert.Contains("rejected the request (HTTP 400)", ex.Message);
        Assert.Contains("unsupported parameter: foo", ex.Message);
        Assert.DoesNotContain("MaxSinglePassTokens", ex.Message);
    }

    [Fact]
    public async Task ChatClient_ExplainsAFailedRequest()
    {
        // The service answers model management, then the connection drops during the chat request.
        var server = FoundryWithChat(_ => throw new HttpRequestException("Connection reset"));
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, FakeCli.Always(RunningStatus), server));

        var ex = await Assert.ThrowsAsync<LocalModelUnavailableException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }));

        Assert.StartsWith("The request to model 'phi-4-mini-instruct-generic-gpu' failed:", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    /// <summary>The reported setup: the tiny default is loaded, phi-4-mini is only downloaded, plus a speech model.</summary>
    private static FakeServer MachineWithPhiDownloaded()
    {
        var loaded = new List<string> { "qwen2.5-0.5b-instruct-generic-cpu" };
        return new(new[] { "127.0.0.1:5273" }, path => path switch
        {
            "/openai/loadedmodels" => FakeServer.Json(System.Text.Json.JsonSerializer.Serialize(loaded)),
            "/openai/models" => FakeServer.Json("""["qwen2.5-0.5b-instruct-generic-cpu","Phi-4-mini-instruct-generic-gpu:5","whisper-tiny-generic-cpu"]"""),
            _ when path.StartsWith("/openai/load/") => Loaded(loaded, path),
            _ => FakeServer.Json("{}")
        });
    }

    /// <summary>Records a load like the real service: the model then appears in /openai/loadedmodels.</summary>
    private static HttpResponseMessage Loaded(List<string> loaded, string path)
    {
        loaded.Add(Uri.UnescapeDataString(path["/openai/load/".Length..].Split('?')[0]));
        return FakeServer.Json("{}");
    }

    [Fact]
    public async Task PrefersDownloadedPhiOverALoadedTinyModel_AndLoadsIt()
    {
        var server = MachineWithPhiDownloaded();
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.True(status.IsAvailable, status.Problem);
        Assert.Equal("Phi-4-mini-instruct-generic-gpu:5", status.ModelId);
        Assert.Contains(server.Requests, r => r.StartsWith("/openai/load/Phi-4-mini-instruct-generic-gpu"));
    }

    [Fact]
    public async Task ListsDownloadedChatModels_LoadedFirst()
    {
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), MachineWithPhiDownloaded());

        var list = await service.ListModelsAsync();

        Assert.Null(list.Problem);
        Assert.Equal(new[]
        {
            new LocalModelInfo("qwen2.5-0.5b-instruct-generic-cpu", IsLoaded: true),
            new LocalModelInfo("Phi-4-mini-instruct-generic-gpu:5", IsLoaded: false)
        }, list.Models);
    }

    [Fact]
    public async Task ListingModels_StartsTheServiceWhenItIsStopped()
    {
        var cli = LegacyCliStoppedUntilStarted();
        using var service = new FoundryLocalService(Options(), cli, MachineWithPhiDownloaded());

        var list = await service.ListModelsAsync();

        Assert.Contains("service start", cli.Commands);
        Assert.Equal(2, list.Models.Count);
    }

    [Fact]
    public async Task ListingModels_ExplainsWhenNothingIsDownloaded()
    {
        // Foundry Local 0.x with an empty cache: only its own routes exist.
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path.StartsWith("/openai/") ? FakeServer.Json("[]") : FakeServer.NotFound());
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        var list = await service.ListModelsAsync();

        Assert.Empty(list.Models);
        Assert.Contains("foundry model download phi-4-mini", list.Problem);
    }

    [Fact]
    public async Task UserChoiceOverridesAutomaticSelection_AndAutomaticCanBeRestored()
    {
        var server = MachineWithPhiDownloaded();
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        service.SelectModel("qwen2.5-0.5b-instruct-generic-cpu");
        Assert.Equal("qwen2.5-0.5b-instruct-generic-cpu", (await service.CheckAsync(ensureModelLoaded: true)).ModelId);
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("/openai/load/")); // already loaded

        service.SelectModel(null);
        Assert.Equal("Phi-4-mini-instruct-generic-gpu:5", (await service.CheckAsync(ensureModelLoaded: false)).ModelId);
    }

    [Theory]
    [InlineData("""{"model":"m","max_completion_tokens":50}""", """{"model":"m","max_tokens":50}""")]
    [InlineData("""{"model":"m","max_tokens":10,"max_completion_tokens":50}""", """{"model":"m","max_tokens":10}""")]
    public void Policy_RenamesMaxCompletionTokens(string input, string expected)
    {
        var output = MaxTokensCompatibilityPolicy.RewriteBody(System.Text.Encoding.UTF8.GetBytes(input));
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(output!));
    }

    [Theory]
    [InlineData("""{"model":"m"}""")]
    [InlineData("not json")]
    public void Policy_LeavesOtherBodiesUntouched(string input)
    {
        Assert.Null(MaxTokensCompatibilityPolicy.RewriteBody(System.Text.Encoding.UTF8.GetBytes(input)));
    }
}
