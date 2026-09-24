using System.Net;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

/// <summary>
/// Foundry Local 1.x+ ("foundry server"): new CLI output, /status + /models/* routes, aliases for load/unload,
/// full variant ids for chat. The CLI texts below are copied from a real machine.
/// </summary>
public class FoundryServerTests
{
    private const string ServerStatus = """
        State    Ready
        PID      11036
        Started  2026-09-24 11:42:21Z
        Uptime   39m 46s
        Web URLs http://127.0.0.1:56294
        """;

    private const string ModelList = """
        ╭───────────────────────────────────┬────────────┬─────────┬────────┬───────┬────────╮
        │ Model Name                        │ Type       │ Size    │ Device │ Tools │ Cached │
        ├───────────────────────────────────┼────────────┼─────────┼────────┼───────┼────────┤
        │ qwen3-0.6b                        │ Chat       │ 529 MB  │ GPU    │ ●     │ ●      │
        │ phi-4-mini                        │ Chat       │ 3.7 GB  │ GPU    │ ●     │ ●      │
        │ qwen2.5-0.5b                      │ Chat       │ 822 MB  │ CPU    │ ●     │ ●      │
        │ qwen2.5-coder-0.5b                │ Chat       │ 365 MB  │ GPU    │ ●     │ ●      │
        │ deepseek-r1-7b                    │ Chat       │ 5.6 GB  │ GPU    │ ○     │ ○      │
        │ phi-4-mini-reasoning              │ Chat       │ 3.1 GB  │ GPU    │ ●     │ ○      │
        │ qwen3-embedding-0.6b              │ Embedding  │ 515 MB  │ GPU    │ ○     │ ○      │
        │ whisper-tiny                      │ Speech     │ 131 MB  │ CPU    │ ○     │ ○      │
        ╰───────────────────────────────────┴────────────┴─────────┴────────┴───────┴────────╯
        """;

    /// <summary>Variant ids the server loads for each alias (its /models/loaded reports these).</summary>
    private static readonly Dictionary<string, string> Variants = new(StringComparer.Ordinal)
    {
        ["phi-4-mini"] = "Phi-4-mini-instruct-generic-gpu:5",
        ["qwen3-0.6b"] = "qwen3-0.6b-generic-gpu:1",
        ["qwen2.5-0.5b"] = "qwen2.5-0.5b-instruct-generic-cpu:4",
        ["qwen2.5-coder-0.5b"] = "qwen2.5-coder-0.5b-instruct-generic-gpu:4",
    };

    /// <summary>Fake of the new server: only its real routes exist; load takes an alias and must be cached.</summary>
    private sealed class FoundryServer : HttpMessageHandler
    {
        public List<string> Loaded { get; } = new();
        public List<string> Requests { get; } = new();
        public List<string> ChatBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Authority != "127.0.0.1:56294") throw new HttpRequestException($"Connection refused ({uri.Authority})");

            var path = uri.AbsolutePath;
            Requests.Add(uri.PathAndQuery);
            var name = Uri.UnescapeDataString(path.Split('/').Last());

            switch (path)
            {
                case "/status":
                    return Json("""{"modelCachePath":"C:\\Users\\me\\.foundry\\cache","endpoints":["http://127.0.0.1:56294"],"pid":11036}""");
                case "/models/loaded":
                    return Json(System.Text.Json.JsonSerializer.Serialize(Loaded));
                case "/v1/chat/completions":
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    ChatBodies.Add(body);
                    var model = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
                    return Loaded.Contains(model)
                        ? Json($$"""{"id":"c1","object":"chat.completion","created":1,"model":"{{model}}","choices":[{"index":0,"message":{"role":"assistant","content":"SUMMARY"},"finish_reason":"stop"}]}""")
                        : Error(HttpStatusCode.BadRequest, "Model not loaded");
            }

            if (path.StartsWith("/models/load/"))
            {
                if (name == "deepseek-r1-7b") return Error(HttpStatusCode.BadRequest, "Model not cached"); // in the catalog, not downloaded
                if (!Variants.TryGetValue(name, out var variant)) return Error(HttpStatusCode.NotFound, "Model not found");
                if (!Loaded.Contains(variant)) Loaded.Add(variant);
                return Json("""{"status":"loaded"}""");
            }

            if (path.StartsWith("/models/unload/"))
            {
                if (Variants.TryGetValue(name, out var variant)) Loaded.Remove(variant);
                return Json("""{"status":"unloaded"}""");
            }

            return Error(HttpStatusCode.NotFound, "Not found"); // e.g. the 0.x /openai/* routes
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

        private static HttpResponseMessage Error(HttpStatusCode code, string message) =>
            new(code) { Content = new StringContent($$$"""{"error":{"message":"{{{message}}}","type":"invalid_request_error"}}""") };
    }

    private sealed class ServerCli : IFoundryCli
    {
        public bool Running { get; set; } = true;
        public List<string> Commands { get; } = new();

        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments);
            if (arguments == "server start") Running = true;
            var output = arguments switch
            {
                "server status" => Running ? ServerStatus : "State    Stopped",
                "server start" => "Server started.",
                "model list" => ModelList,
                _ => ""
            };
            return Task.FromResult(new FoundryCliResult(true, output, null));
        }
    }

    private static FoundryOptions Options()
    {
        var options = new FoundryOptions();
        options.Local.ModelId = "qwen2.5-0.5b-instruct-generic-cpu";
        return options;
    }

    [Fact]
    public void ParsesTheNewStatusOutput()
    {
        Assert.Equal("http://127.0.0.1:56294/", FoundryCli.ParseServiceUri(ServerStatus)?.ToString());
        Assert.Null(FoundryCli.ParseServiceUri("State    Stopped"));
    }

    [Fact]
    public void ParsesTheModelTable()
    {
        var rows = FoundryModelTable.Parse(ModelList);

        Assert.Equal(8, rows.Count);
        Assert.Equal(new FoundryModelRow("phi-4-mini", "Chat", true), rows[1]);
        Assert.Equal(new FoundryModelRow("deepseek-r1-7b", "Chat", false), rows[4]);
    }

    [Theory]
    [InlineData("Phi-4-mini-instruct-generic-gpu:5", "phi-4-mini")]
    [InlineData("phi-4-mini-reasoning-generic-gpu:1", "phi-4-mini-reasoning")]
    [InlineData("qwen2.5-coder-0.5b-instruct-generic-gpu:4", "qwen2.5-coder-0.5b")]
    [InlineData("qwen2.5-0.5b-instruct-generic-cpu", "qwen2.5-0.5b")]
    [InlineData("llama3.2", null)]
    public void MapsFullIdsToAliases(string id, string? alias)
    {
        var aliases = FoundryModelTable.Parse(ModelList).Select(r => r.Alias);
        Assert.Equal(alias, FoundryModelTable.AliasOf(id, aliases));
    }

    [Fact]
    public async Task ListsDownloadedChatModelsByAlias()
    {
        var server = new FoundryServer();
        server.Loaded.Add("Phi-4-mini-instruct-generic-gpu:5");
        using var service = new FoundryLocalService(Options(), new ServerCli(), server);

        var list = await service.ListModelsAsync();

        Assert.Null(list.Problem);
        Assert.Equal(new[]
        {
            new LocalModelInfo("phi-4-mini", IsLoaded: true),
            new LocalModelInfo("qwen2.5-0.5b", IsLoaded: false),
            new LocalModelInfo("qwen2.5-coder-0.5b", IsLoaded: false),
            new LocalModelInfo("qwen3-0.6b", IsLoaded: false)
        }, list.Models);
    }

    [Fact]
    public async Task AModelLoadedFromTheTerminalCountsAsLoaded_AndChatUsesItsFullId()
    {
        var server = new FoundryServer();
        server.Loaded.Add("Phi-4-mini-instruct-generic-gpu:5");   // e.g. 'foundry model run phi-4-mini'
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, new ServerCli(), server));

        Assert.Equal(ActiveModelStateKind.Loaded, (await client.GetActiveModelStateAsync()).Kind);   // auto-selected phi-4-mini
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize.") });

        Assert.Equal("SUMMARY", response.Text);
        Assert.Equal("phi-4-mini", client.ActiveModelId);
        Assert.Contains("\"model\":\"Phi-4-mini-instruct-generic-gpu:5\"", Assert.Single(server.ChatBodies));
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("/models/load/"));             // no needless reload
    }

    [Fact]
    public async Task StartsTheServer_LoadsByAlias_AndSwitchUnloadsThePreviousModel()
    {
        var server = new FoundryServer();
        var cli = new ServerCli { Running = false };
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, cli, server));

        var first = await client.SwitchModelAsync("qwen3-0.6b");
        Assert.True(first.Status.IsAvailable, first.Status.Problem);
        Assert.Contains("server start", cli.Commands);
        Assert.Contains("/models/load/qwen3-0.6b", server.Requests);

        var second = await client.SwitchModelAsync("phi-4-mini");
        Assert.True(second.Status.IsAvailable, second.Status.Problem);
        Assert.Contains("/models/unload/qwen3-0.6b", server.Requests);
        Assert.Equal(new[] { "Phi-4-mini-instruct-generic-gpu:5" }, server.Loaded);
    }

    [Fact]
    public async Task ASavedFullIdIsLoadedByItsAlias()
    {
        var server = new FoundryServer();
        var options = Options();
        using var service = new FoundryLocalService(options, new ServerCli(), server);
        service.SelectModel("Phi-4-mini-instruct-generic-gpu");        // saved by an earlier version of the app

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.True(status.IsAvailable, status.Problem);
        Assert.Equal("phi-4-mini", status.ModelId);
        Assert.Contains("/models/load/phi-4-mini", server.Requests);
    }

    [Fact]
    public async Task ExplainsAModelThatIsNotDownloaded()
    {
        var options = Options();
        using var service = new FoundryLocalService(options, new ServerCli(), new FoundryServer());
        service.SelectModel("deepseek-r1-7b");

        var status = await service.CheckAsync(ensureModelLoaded: true);

        Assert.False(status.IsAvailable);
        Assert.Contains("'deepseek-r1-7b' is not downloaded", status.Problem);
        Assert.Contains("foundry model download deepseek-r1-7b", status.Problem);
    }

    [Fact]
    public async Task ReloadsAndRetriesWhenTheServerUnloadedTheModel()
    {
        var server = new FoundryServer();
        var options = Options();
        using var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, new ServerCli(), server));
        await client.SwitchModelAsync("phi-4-mini");

        // Unloaded behind the app's back (idle time-to-live).
        server.Loaded.Clear();
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize.") });

        Assert.Equal("SUMMARY", response.Text);
        Assert.Equal(new[] { "Phi-4-mini-instruct-generic-gpu:5" }, server.Loaded);
    }
}
