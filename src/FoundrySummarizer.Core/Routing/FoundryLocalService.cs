using System.ClientModel;
using System.ClientModel.Primitives;
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

/// <summary>A chat model available on this machine.</summary>
/// <param name="Id">Model id as the service reports it, e.g. "Phi-4-mini-instruct-generic-gpu:5".</param>
/// <param name="IsLoaded">True when the model is already in memory and answers without a load delay.</param>
public record LocalModelInfo(string Id, bool IsLoaded);

/// <summary>Result of listing the models on this machine.</summary>
/// <param name="Models">Downloaded chat models, loaded ones first; empty when the service is unreachable.</param>
/// <param name="Problem">Why the list could not be read; null on success.</param>
public record LocalModelList(IReadOnlyList<LocalModelInfo> Models, string? Problem);

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
    private readonly PipelineTransport? _chatTransport;

    // Checks run before every request and may run concurrently (summary + chat); one at a time keeps the
    // discovered endpoint, selected model and client consistent.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Uri? _serviceBase;
    private string _activeModelId;
    private IChatClient? _client;
    private string? _clientKey;
    private string? _userSelectedModelId;

    // Speech, embedding and image models share the listing but cannot answer chat requests.
    private static readonly string[] NonChatMarkers = { "whisper", "embed", "tts", "vision-encoder" };

    /// <param name="options">Local endpoint, model and timeout settings.</param>
    /// <param name="cli">Foundry CLI runner; defaults to the real <c>foundry</c> executable.</param>
    /// <param name="probeHandler">HTTP handler for status, model-management and chat calls; tests pass a stub, null uses the network.</param>
    public FoundryLocalService(FoundryOptions options, IFoundryCli? cli = null, HttpMessageHandler? probeHandler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cli = cli ?? new FoundryCli();
        _probeClient = probeHandler is null ? new HttpClient() : new HttpClient(probeHandler);
        // Model loads can take minutes; each call sets its own shorter timeout where appropriate.
        _probeClient.Timeout = Timeout.InfiniteTimeSpan;
        _catalog = new LocalModelCatalog(_probeClient);
        _chatTransport = probeHandler is null ? null : new HttpClientPipelineTransport(new HttpClient(probeHandler, disposeHandler: false));
        _activeModelId = options.LocalModelId;
    }

    /// <summary>The model requests are sent to (configured, or the best available when auto-selection is on).</summary>
    public string ActiveModelId => _activeModelId;

    /// <summary>The model the user chose, or null to choose automatically from the preferences.</summary>
    public string? UserSelectedModelId => _userSelectedModelId;

    /// <summary>
    /// Uses <paramref name="modelId"/> for all further requests, or returns to automatic selection when null.
    /// The model is loaded on the next <see cref="CheckAsync"/> with <c>ensureModelLoaded</c>.
    /// </summary>
    /// <param name="modelId">A model id from <see cref="ListModelsAsync"/>, or null for automatic.</param>
    public void SelectModel(string? modelId)
    {
        _userSelectedModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        if (_userSelectedModelId is not null)
        {
            _activeModelId = _userSelectedModelId;
        }
    }

    /// <summary>
    /// Lists the chat models downloaded on this machine, finding (and, if configured, starting) the service first.
    /// </summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    public async Task<LocalModelList> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (serviceBase, problem) = await FindReachableServiceAsync(cancellationToken);
            if (serviceBase is null)
            {
                return new LocalModelList(Array.Empty<LocalModelInfo>(), problem);
            }

            var loaded = await _catalog.TryGetModelIdsAsync(serviceBase, "/openai/loadedmodels", cancellationToken) ?? Array.Empty<string>();
            var downloaded = await ListDownloadedAsync(serviceBase, cancellationToken);
            // Listed once each even when the two endpoints spell the id with and without a version suffix.
            var models = downloaded.Concat(loaded)
                .Where(IsChatModel)
                .DistinctBy(StripVersion, StringComparer.OrdinalIgnoreCase)
                .Select(id => new LocalModelInfo(id, loaded.Any(l => SameModel(l, id))))
                .OrderByDescending(m => m.IsLoaded)
                .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new LocalModelList(models, models.Count == 0
                ? "No models are downloaded. Download one with 'foundry model download phi-4-mini'."
                : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks Foundry Local to release <paramref name="modelId"/> from memory (<c>/openai/unload/{model}</c>), freeing
    /// GPU/RAM for the next model. Servers without the route (e.g. Ollama) are treated as success.
    /// </summary>
    /// <returns>Null on success or when there is nothing to unload; otherwise why unloading failed.</returns>
    public async Task<string?> UnloadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_serviceBase is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var url = new Uri(_serviceBase, $"openai/unload/{Uri.EscapeDataString(modelId)}?force=true");
            try
            {
                using var response = await _probeClient.GetAsync(url, cts.Token);
                // 404: the model was not loaded, or the server has no unload route; either way nothing to free.
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? null
                    : $"Foundry Local could not unload '{modelId}' (HTTP {(int)response.StatusCode}); it stays in memory until its idle timeout.";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return $"Unloading '{modelId}' did not finish within 60s; it stays in memory until its idle timeout.";
            }
            catch (HttpRequestException ex)
            {
                return $"Unloading '{modelId}' failed: {ex.Message}";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the active model again, e.g. after the service answered "model is not loaded" because it unloaded
    /// the model after its idle time-to-live.
    /// </summary>
    /// <returns>Null on success; otherwise what went wrong and how to fix it.</returns>
    public async Task<string?> ReloadActiveModelAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _serviceBase is null
                ? "The local model service has not been found yet."
                : await LoadModelAsync(_serviceBase, _activeModelId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// True when two ids name the same model. Foundry Local reports some ids with a catalog version suffix
    /// ("Phi-4-mini-instruct-generic-gpu:5") and others without, depending on the endpoint. Only an all-digit
    /// suffix is ignored, so Ollama tags such as "llama3.2:3b" still have to match exactly.
    /// </summary>
    public static bool SameModel(string a, string b) =>
        string.Equals(StripVersion(a), StripVersion(b), StringComparison.OrdinalIgnoreCase);

    private static string StripVersion(string id)
    {
        int colon = id.LastIndexOf(':');
        return colon > 0 && colon < id.Length - 1 && id.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') < 0
            ? id[..colon]
            : id;
    }

    private static bool IsChatModel(string id) =>
        !NonChatMarkers.Any(marker => id.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Foundry Local lists downloaded (cached) models at /openai/models; other servers at /v1/models.</summary>
    private async Task<IReadOnlyList<string>> ListDownloadedAsync(Uri serviceBase, CancellationToken cancellationToken) =>
        await _catalog.TryGetModelIdsAsync(serviceBase, "/openai/models", cancellationToken)
        ?? await _catalog.TryGetModelIdsAsync(serviceBase, "/v1/models", cancellationToken)
        ?? Array.Empty<string>();

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

            // Checked against the service on every call (never cached): Foundry Local unloads idle models after
            // their time-to-live, so a model loaded earlier in the session may be gone now.
            if (ensureModelLoaded && isFoundry && !loaded!.Any(id => SameModel(id, _activeModelId)))
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

    /// <summary>
    /// Chooses the model: the user's choice; else the best preferred model that is loaded; else the best preferred
    /// model that is downloaded (it is loaded before use); else the configured <c>Local.ModelId</c>.
    /// A weak model that happens to be loaded never outranks a preferred one on disk: loading takes a minute once,
    /// while a sub-1B model gives poor summaries every time.
    /// </summary>
    private async Task SelectModelAsync(Uri serviceBase, IReadOnlyList<string>? loaded, CancellationToken cancellationToken)
    {
        if (_userSelectedModelId is not null)
        {
            _activeModelId = _userSelectedModelId;
            return;
        }

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
        }

        var downloaded = await ListDownloadedAsync(serviceBase, cancellationToken);
        _activeModelId = LocalModelSelector.Select(downloaded, preferences, _options.LocalModelId);
    }

    private IChatClient GetOrCreateClient(Uri serviceBase)
    {
        var key = $"{serviceBase}|{_activeModelId}";
        if (_client is not null && _clientKey == key) return _client;

        _client?.Dispose();
        _client = CreateChatClient(new Uri(serviceBase, "v1"), _activeModelId, TimeSpan.FromSeconds(Math.Max(1, _options.Local.TimeoutSeconds)), _chatTransport);
        _clientKey = key;
        return _client;
    }

    /// <summary>
    /// Creates an OpenAI-compatible chat client for a local server, with the request fix-ups local servers need
    /// (see <see cref="MaxTokensCompatibilityPolicy"/>).
    /// </summary>
    /// <param name="endpoint">The OpenAI-compatible base address (…/v1).</param>
    /// <param name="modelId">Model to send requests to.</param>
    /// <param name="networkTimeout">Maximum wait for one response.</param>
    /// <param name="transport">HTTP transport; tests pass a fake, null uses the default.</param>
    public static IChatClient CreateChatClient(Uri endpoint, string modelId, TimeSpan networkTimeout, PipelineTransport? transport = null)
    {
        var clientOptions = new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = networkTimeout };
        if (transport is not null) clientOptions.Transport = transport;
        clientOptions.AddPolicy(new MaxTokensCompatibilityPolicy(), PipelinePosition.PerCall);

        return new OpenAIClient(new ApiKeyCredential("local-foundry-key"), clientOptions).GetChatClient(modelId).AsIChatClient();
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
