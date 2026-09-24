using System.ClientModel;
using System.Net.Http;
using Microsoft.Extensions.AI;
using OpenAI;

namespace FoundrySummarizer.Core.Routing;

/// <summary>State of the local model service after a check.</summary>
/// <param name="IsAvailable">True when requests can be sent to <paramref name="Client"/>.</param>
/// <param name="Endpoint">The OpenAI-compatible endpoint in use (…/v1), if one was found.</param>
/// <param name="ModelId">The model requests are sent to.</param>
/// <param name="Problem">When unavailable, what is wrong and how to fix it, in plain language.</param>
/// <param name="Client">Chat client for the model; null when unavailable.</param>
public record LocalModelStatus(bool IsAvailable, Uri? Endpoint, string ModelId, string? Problem, IChatClient? Client);

/// <summary>
/// Finds, starts and checks Foundry Local (or another OpenAI-compatible local server such as Ollama), picks the
/// model and makes sure it is loaded. Every failure is reported with a reason, so the app can tell the user why
/// no model answered instead of silently falling back.
/// </summary>
public sealed class FoundryLocalService : IDisposable
{
    private readonly FoundryOptions _options;
    private readonly IFoundryCli _cli;
    private readonly HttpClient _probeClient;
    private readonly LocalModelCatalog _catalog;

    // Checks run before every request and may run concurrently (summary + chat); one at a time keeps the
    // discovered endpoint, selected model and client consistent.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _confirmedLoaded = new(StringComparer.OrdinalIgnoreCase);

    private Uri? _serviceBase;
    private string _activeModelId;
    private IChatClient? _client;
    private string? _clientKey;

    /// <param name="options">Local endpoint, model and timeout settings.</param>
    /// <param name="cli">Foundry CLI runner; defaults to the real <c>foundry</c> executable.</param>
    /// <param name="probeHandler">HTTP handler for status and model-management calls (tests pass a stub).</param>
    public FoundryLocalService(FoundryOptions options, IFoundryCli? cli = null, HttpMessageHandler? probeHandler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cli = cli ?? new FoundryCli();
        _probeClient = probeHandler is null ? new HttpClient() : new HttpClient(probeHandler);
        // Model loads can take minutes; each call sets its own shorter timeout where appropriate.
        _probeClient.Timeout = Timeout.InfiniteTimeSpan;
        _catalog = new LocalModelCatalog(_probeClient);
        _activeModelId = options.LocalModelId;
    }

    /// <summary>The model requests are sent to (configured, or the best available when auto-selection is on).</summary>
    public string ActiveModelId => _activeModelId;

    /// <summary>The OpenAI-compatible endpoint last found, or null before the first successful check.</summary>
    public Uri? Endpoint => _serviceBase is null ? null : new Uri(_serviceBase, "v1");

    /// <summary>
    /// Checks that the local service is reachable and picks the model.
    /// </summary>
    /// <param name="ensureModelLoaded">Also load the model into Foundry Local if needed (slow; do this before real requests, not for status polling).</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<LocalModelStatus> CheckAsync(bool ensureModelLoaded, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (serviceBase, problem) = await FindReachableServiceAsync(cancellationToken);
            if (serviceBase is null)
            {
                return Unavailable(problem!);
            }

            var loaded = await _catalog.TryGetModelIdsAsync(serviceBase, "/openai/loadedmodels", cancellationToken);
            bool isFoundry = loaded is not null;
            await SelectModelAsync(serviceBase, loaded, cancellationToken);

            if (ensureModelLoaded && isFoundry && !loaded!.Contains(_activeModelId, StringComparer.OrdinalIgnoreCase)
                && !_confirmedLoaded.Contains(_activeModelId))
            {
                var loadProblem = await LoadModelAsync(serviceBase, _activeModelId, cancellationToken);
                if (loadProblem is not null)
                {
                    return Unavailable(loadProblem);
                }
            }

            return new LocalModelStatus(true, Endpoint, _activeModelId, null, GetOrCreateClient(serviceBase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks Foundry Local to load <paramref name="modelId"/> into memory (<c>/openai/load/{model}</c>).
    /// </summary>
    /// <returns>Null on success; otherwise what went wrong and how to fix it.</returns>
    public async Task<string?> LoadModelAsync(Uri serviceBase, string modelId, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.Local.ModelLoadTimeoutSeconds)));
        var url = new Uri(serviceBase, $"openai/load/{Uri.EscapeDataString(modelId)}");
        try
        {
            using var response = await _probeClient.GetAsync(url, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                _confirmedLoaded.Add(modelId);
                return null;
            }

            var body = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            return $"Foundry Local could not load model '{modelId}' (HTTP {(int)response.StatusCode}{(body.Length > 0 ? $": {Truncate(body)}" : "")}). " +
                   $"Check the name with 'foundry model list' and download it with 'foundry model download {modelId}'.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Model '{modelId}' did not finish loading within {_options.Local.ModelLoadTimeoutSeconds}s. Load it first with 'foundry model run {modelId}'.";
        }
        catch (HttpRequestException ex)
        {
            return $"Loading model '{modelId}' failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Finds the service base address and confirms something answers there. With auto-discovery, a failed probe
    /// triggers one re-discovery, because Foundry Local moves to a new port whenever its service restarts.
    /// </summary>
    private async Task<(Uri? ServiceBase, string? Problem)> FindReachableServiceAsync(CancellationToken cancellationToken)
    {
        bool discover = _options.Local.AutoDiscover || IsAuto(_options.Local.Endpoint);
        var notes = new List<string>();

        _serviceBase ??= await DiscoverAsync(discover, notes, cancellationToken);
        var probeProblem = await ProbeAsync(_serviceBase, cancellationToken);
        if (probeProblem is null)
        {
            return (_serviceBase, null);
        }

        if (discover)
        {
            var rediscovered = await DiscoverAsync(discover, notes, cancellationToken);
            if (rediscovered != _serviceBase)
            {
                _serviceBase = rediscovered;
                probeProblem = await ProbeAsync(_serviceBase, cancellationToken);
                if (probeProblem is null)
                {
                    return (_serviceBase, null);
                }
            }
        }

        var detail = string.Join(" ", notes.Distinct());
        return (null, $"{probeProblem}{(detail.Length > 0 ? " " + detail : "")}");
    }

    /// <summary>Works out where the service should be: CLI status, then (optionally) starting it, then configuration.</summary>
    private async Task<Uri> DiscoverAsync(bool discover, List<string> notes, CancellationToken cancellationToken)
    {
        if (discover)
        {
            var status = await _cli.RunAsync("service status", TimeSpan.FromSeconds(15), cancellationToken);
            var found = FoundryCli.ParseServiceUri(status.Output);
            if (found is not null) return found;

            if (status.Problem is not null && status.Output.Length == 0)
            {
                notes.Add(status.Problem);                       // CLI not installed or failed to run
            }
            else if (_options.Local.AutoStartService)
            {
                var start = await _cli.RunAsync("service start", TimeSpan.FromSeconds(90), cancellationToken);
                found = FoundryCli.ParseServiceUri(start.Output)
                        ?? FoundryCli.ParseServiceUri((await _cli.RunAsync("service status", TimeSpan.FromSeconds(15), cancellationToken)).Output);
                if (found is not null) return found;
                notes.Add(start.Problem is null
                    ? "Foundry Local did not report a service address after 'foundry service start'."
                    : $"Starting Foundry Local failed: {start.Problem}");
            }
            else
            {
                notes.Add("Foundry Local is not running. Start it with 'foundry service start'.");
            }
        }

        // Explicit configuration (or the legacy ~/.foundry/daemon.json lookup) when discovery found nothing.
        var configured = new Uri(_options.GetEffectiveLocalEndpoint());
        return new Uri($"{configured.Scheme}://{configured.Authority}");
    }

    /// <summary>
    /// Any HTTP response means a server is listening; only connection failures and timeouts count as down.
    /// (Foundry Local answers <c>/openai/status</c>; Ollama returns 404 there but is still up.)
    /// </summary>
    /// <returns>Null when reachable; otherwise the reason.</returns>
    private async Task<string?> ProbeAsync(Uri serviceBase, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _probeClient.GetAsync(new Uri(serviceBase, "openai/status"), cts.Token);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"The local model service at {serviceBase} did not respond within 5s.";
        }
        catch (HttpRequestException ex)
        {
            return $"No local model service is reachable at {serviceBase} ({ex.Message}).";
        }
    }

    /// <summary>Chooses the model: best preferred loaded model, else best preferred cached/listed model, else configured.</summary>
    private async Task SelectModelAsync(Uri serviceBase, IReadOnlyList<string>? loaded, CancellationToken cancellationToken)
    {
        if (!_options.Local.AutoSelectModel)
        {
            _activeModelId = _options.LocalModelId;
            return;
        }

        var preferences = _options.Local.GetPreferredModels();
        if (loaded is { Count: > 0 })
        {
            var fromLoaded = LocalModelSelector.Select(loaded, preferences, configuredModelId: string.Empty);
            if (fromLoaded.Length > 0)
            {
                _activeModelId = fromLoaded;
                return;
            }

            // A loaded model outranks an unloaded preferred one: it answers immediately.
            if (loaded.Contains(_options.LocalModelId, StringComparer.OrdinalIgnoreCase))
            {
                _activeModelId = _options.LocalModelId;
                return;
            }
        }

        var listed = await _catalog.TryGetModelIdsAsync(serviceBase, "/openai/models", cancellationToken)
                     ?? await _catalog.TryGetModelIdsAsync(serviceBase, "/v1/models", cancellationToken)
                     ?? Array.Empty<string>();
        _activeModelId = LocalModelSelector.Select(listed, preferences, _options.LocalModelId);
    }

    private IChatClient GetOrCreateClient(Uri serviceBase)
    {
        var key = $"{serviceBase}|{_activeModelId}";
        if (_client is not null && _clientKey == key) return _client;

        _client?.Dispose();
        var openAi = new OpenAIClient(
            new ApiKeyCredential("local-foundry-key"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(serviceBase, "v1"),
                NetworkTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.Local.TimeoutSeconds))
            });
        _client = openAi.GetChatClient(_activeModelId).AsIChatClient();
        _clientKey = key;
        return _client;
    }

    private LocalModelStatus Unavailable(string problem) => new(false, Endpoint, _activeModelId, problem, null);

    private static bool IsAuto(string endpoint) => string.Equals(endpoint, "auto", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";

    public void Dispose()
    {
        _client?.Dispose();
        _probeClient.Dispose();
        _gate.Dispose();
    }
}
