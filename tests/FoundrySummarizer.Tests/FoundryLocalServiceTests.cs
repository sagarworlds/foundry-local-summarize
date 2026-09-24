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

        public FakeServer(IEnumerable<string> liveAuthorities, Func<string, HttpResponseMessage> respond)
        {
            _liveAuthorities = liveAuthorities.ToHashSet();
            _respond = respond;
        }

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (!_liveAuthorities.Contains(uri.Authority))
            {
                throw new HttpRequestException($"Connection refused ({uri.Authority})");
            }

            Requests.Add(uri.PathAndQuery);
            return Task.FromResult(_respond(uri.PathAndQuery));
        }

        public static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) };

        public static HttpResponseMessage NotFound(string body = "") =>
            new(HttpStatusCode.NotFound) { Content = new StringContent(body) };
    }

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
        var cli = new FakeCli((args, call) => args switch
        {
            "service status" when call == 1 => new FoundryCliResult(true, "🔴 Model management service is not running!", null),
            "service start" => new FoundryCliResult(true, "Service started.", null),
            _ => new FoundryCliResult(true, RunningStatus, null)
        });
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, _ => FakeServer.Json("[]"));
        using var service = new FoundryLocalService(Options(), cli, server);

        var status = await service.CheckAsync(ensureModelLoaded: false);

        Assert.True(status.IsAvailable, status.Problem);
        Assert.Equal(new[] { "service status", "service start", "service status" }, cli.Commands);
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
        Assert.Contains("/openai/load/qwen2.5-0.5b-instruct-generic-cpu", server.Requests);
        Assert.Contains("could not load model 'qwen2.5-0.5b-instruct-generic-cpu' (HTTP 404: model not found in cache)", status.Problem);
        Assert.Contains("foundry model download", status.Problem);
    }

    [Fact]
    public async Task Loads_ModelOnce()
    {
        var server = new FakeServer(new[] { "127.0.0.1:5273" }, path => path switch
        {
            "/openai/loadedmodels" => FakeServer.Json("[]"),
            _ when path.StartsWith("/openai/load/") => FakeServer.Json("{}"),
            _ => FakeServer.Json("[]")
        });
        using var service = new FoundryLocalService(Options(), FakeCli.Always(RunningStatus), server);

        Assert.True((await service.CheckAsync(ensureModelLoaded: true)).IsAvailable);
        Assert.True((await service.CheckAsync(ensureModelLoaded: true)).IsAvailable);

        Assert.Single(server.Requests, r => r.StartsWith("/openai/load/"));
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
    public async Task Router_ExplainsWhyItFellBackToDemoOutput()
    {
        var server = new FakeServer(Array.Empty<string>(), _ => FakeServer.NotFound());
        var options = Options();
        var router = new HybridChatClientRouter(options, new FoundryLocalService(options, FakeCli.NotInstalled(), server));

        var response = await router.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Summarize: budget is $150,000.") });

        Assert.StartsWith(HybridChatClientRouter.FallbackNotice, response.Text);
        Assert.Contains("> **Why:** No local model service is reachable at http://127.0.0.1:63715/", response.Text);
        Assert.Equal(router.LastFallbackReason, router.LastRoutingDecision?.Rationale.Split("⚠️ ")[1].Split(" The output is canned")[0]);
    }
}
