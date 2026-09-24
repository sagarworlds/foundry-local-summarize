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

/// <summary>Whether the active model is in memory, per <see cref="FoundryLocalService.GetActiveModelStateAsync"/>.</summary>
public enum ActiveModelStateKind
{
    /// <summary>The model is in Foundry Local's memory.</summary>
    Loaded,

    /// <summary>The service answers, but the model is not in memory (e.g. unloaded after its idle time-to-live).</summary>
    NotLoaded,

    /// <summary>The service could not be reached or could not list its loaded models.</summary>
    Unknown
}

/// <summary>Result of <see cref="FoundryLocalService.GetActiveModelStateAsync"/>.</summary>
/// <param name="Kind">Loaded, not loaded, or unknown.</param>
/// <param name="Problem">For <see cref="ActiveModelStateKind.Unknown"/>, what failed; otherwise null.</param>
public record ActiveModelState(ActiveModelStateKind Kind, string? Problem);

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
/// no model answered instead of silently falling back. Version differences (Foundry Local 0.x "foundry service"
/// vs 1.x+ "foundry server") are handled by the <see cref="IModelManagementApi"/> detected for the server.
/// </summary>
public sealed class FoundryLocalService : IDisposable
{
    private readonly FoundryOptions _options;
    private readonly IFoundryCli _cli;
    private readonly HttpClient _http;
    private readonly PipelineTransport? _chatTransport;

    // Checks run before every request and may run concurrently (summary + chat); one at a time keeps the
    // discovered endpoint, selected model and client consistent.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Uri? _serviceBase;
    private IModelManagementApi? _api;
    private Uri? _apiBase;
    private string _activeModelId;
    private string? _chatModelId;
    private IChatClient? _client;
    private string? _clientKey;
    private string? _userSelectedModelId;

    // Speech, embedding and image models share the listing but cannot answer chat requests.
    private static readonly string[] NonChatMarkers = { "whisper", "embed", "tts", "vision-encoder", "asr", "speech" };

    // A busy service (loading a model, generating on the CPU) can be slow to answer; a timeout means "not responding".
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private const int LoadConfirmAttempts = 4;
    private static readonly TimeSpan LoadConfirmDelay = TimeSpan.FromMilliseconds(500);

    /// <param name="options">Local endpoint, model and timeout settings.</param>
    /// <param name="cli">Foundry CLI runner; defaults to the real <c>foundry</c> executable.</param>
    /// <param name="probeHandler">HTTP handler for status, model-management and chat calls; tests pass a stub, null uses the network.</param>
    public FoundryLocalService(FoundryOptions options, IFoundryCli? cli = null, HttpMessageHandler? probeHandler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cli = cli ?? new FoundryCli();
        _http = probeHandler is null ? new HttpClient() : new HttpClient(probeHandler);
        // Model loads can take minutes; each call sets its own timeout.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _chatTransport = probeHandler is null ? null : new HttpClientPipelineTransport(new HttpClient(probeHandler, disposeHandler: false));
        _activeModelId = options.LocalModelId;
    }

    /// <summary>The selected model (configured, chosen by the user, or the best available when auto-selection is on).</summary>
    public string ActiveModelId => _activeModelId;

    /// <summary>The model the user chose, or null to choose automatically from the preferences.</summary>
    public string? UserSelectedModelId => _userSelectedModelId;

    /// <summary>The OpenAI-compatible endpoint last found, or null before the first successful check.</summary>
    public Uri? Endpoint => _serviceBase is null ? null : new Uri(_serviceBase, "v1");

    /// <summary>
    /// Uses <paramref name="modelId"/> for all further requests, or returns to automatic selection when null.
    /// The model is loaded on the next <see cref="CheckAsync"/> with <c>ensureModelLoaded</c>.
    /// </summary>
    /// <param name="modelId">A model name from <see cref="ListModelsAsync"/>, or null for automatic.</param>
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
            var (api, problem) = await ConnectAsync(cancellationToken);
            if (api is null)
            {
                return new LocalModelList(Array.Empty<LocalModelInfo>(), problem);
            }

            var downloaded = await api.ListDownloadedAsync(cancellationToken);
            if (downloaded.Ids is null)
            {
                return new LocalModelList(Array.Empty<LocalModelInfo>(), $"Could not list the downloaded models of {api.DisplayName}: {downloaded.Error}");
            }

            var loaded = (await api.ListLoadedAsync(cancellationToken)).Ids ?? Array.Empty<string>();
            var models = downloaded.Ids
                .Where(IsChatModel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new LocalModelInfo(name, api.IsLoaded(loaded, name)))
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
    /// Checks that the local service is reachable and picks the model.
    /// </summary>
    /// <param name="ensureModelLoaded">Also load the model if needed (slow; do this before real requests, not for status polling).</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<LocalModelStatus> CheckAsync(bool ensureModelLoaded, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (api, problem) = await ConnectAsync(cancellationToken);
            if (api is null)
            {
                return Unavailable(problem!);
            }

            var loadedListing = await api.ListLoadedAsync(cancellationToken);
            await SelectModelAsync(api, loadedListing.Ids, cancellationToken);
            var loaded = loadedListing.Ids ?? Array.Empty<string>();

            if (ensureModelLoaded && api.SupportsLoading)
            {
                if (loadedListing.Ids is null)
                {
                    // "The server answers" is not enough: without the loaded list the model cannot be confirmed.
                    return Unavailable($"{api.DisplayName} did not report which models are loaded ({loadedListing.Error}), so the app cannot confirm the model is ready. Restart Foundry Local and try again.");
                }

                // Checked against the service on every call (never cached): Foundry Local unloads idle models after
                // their time-to-live, so a model loaded earlier in the session may be gone now.
                if (!api.IsLoaded(loaded, _activeModelId))
                {
                    var (confirmed, loadProblem) = await LoadAndConfirmAsync(api, cancellationToken);
                    if (loadProblem is not null)
                    {
                        return Unavailable(loadProblem);
                    }
                    loaded = confirmed;
                }
            }

            _chatModelId = api.ChatModelId(loaded, _activeModelId);
            return new LocalModelStatus(true, Endpoint, _activeModelId, null, GetOrCreateClient(_serviceBase!, _chatModelId));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether the active model is in memory right now. The service is found again first, so a restart on a new port
    /// is followed, and a failed listing is retried once before it counts as a failure.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<ActiveModelState> GetActiveModelStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (api, problem) = await ConnectAsync(cancellationToken);
            if (api is null)
            {
                return new ActiveModelState(ActiveModelStateKind.Unknown, problem);
            }

            if (!api.SupportsLoading)
            {
                return new ActiveModelState(ActiveModelStateKind.Loaded, null);
            }

            var listing = await api.ListLoadedAsync(cancellationToken);
            if (listing.Ids is null)
            {
                await Task.Delay(LoadConfirmDelay, cancellationToken);
                listing = await api.ListLoadedAsync(cancellationToken);
            }

            if (listing.Ids is null)
            {
                return new ActiveModelState(ActiveModelStateKind.Unknown,
                    $"{api.DisplayName} is running but could not list its loaded models ({listing.Error}).");
            }

            // Same choice and spelling as before a request: with automatic selection, a preferred model loaded
            // outside the app (e.g. 'foundry model run phi-4-mini') becomes the active one.
            await SelectModelAsync(api, listing.Ids, cancellationToken);
            return api.IsLoaded(listing.Ids, _activeModelId)
                ? new ActiveModelState(ActiveModelStateKind.Loaded, null)
                : new ActiveModelState(ActiveModelStateKind.NotLoaded, DescribeLoaded(api, listing.Ids));
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
            var (api, problem) = await ConnectAsync(cancellationToken);
            if (api is null) return problem;

            var (loaded, loadProblem) = await LoadAndConfirmAsync(api, cancellationToken);
            if (loadProblem is null)
            {
                _chatModelId = api.ChatModelId(loaded, _activeModelId);
            }
            return loadProblem;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases <paramref name="modelId"/> from memory, freeing GPU/RAM for the next model. Servers that load models
    /// on demand have nothing to unload and report success.
    /// </summary>
    /// <returns>Null on success or when there is nothing to unload; otherwise why unloading failed.</returns>
    public async Task<string?> UnloadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_serviceBase is null) return null;
            var (api, _) = await ConnectAsync(cancellationToken);
            return api is null ? null : await api.UnloadAsync(await api.NormalizeAsync(modelId, cancellationToken), cancellationToken);
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

    /// <summary>
    /// True when <paramref name="modelId"/> is among <paramref name="loadedNames"/> as reported by Foundry Local 0.x.
    /// Like the official SDK, each reported name is resolved through the catalog first, because the service may
    /// report an alias ("phi-4-mini") or an id without its version rather than the exact id.
    /// </summary>
    public static bool IsInLoadedList(IEnumerable<string> loadedNames, string modelId, FoundryCatalog? catalog)
    {
        var target = catalog?.Resolve(modelId);
        return loadedNames.Any(name =>
            SameModel(name, modelId)
            || (catalog?.Resolve(name) is { } resolved && SameModel(resolved.Id, modelId))
            || (target is not null && target.Alias.Length > 0 && target.Alias.Equals(name, StringComparison.OrdinalIgnoreCase)));
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
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = networkTimeout,
            // The SDK retries timeouts and connection errors 3 times by default. A local model that timed out is still
            // busy with the same request, so each retry waits the full timeout again (4 × 300s by default) before the
            // user sees anything. The app reloads the model itself when it was unloaded (see FoundryLocalChatClient).
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };
        if (transport is not null) clientOptions.Transport = transport;
        clientOptions.AddPolicy(new MaxTokensCompatibilityPolicy(), PipelinePosition.PerCall);

        return new OpenAIClient(new ApiKeyCredential("local-foundry-key"), clientOptions).GetChatClient(modelId).AsIChatClient();
    }

    /// <summary>Finds the service and the model-management API that matches its version.</summary>
    private async Task<(IModelManagementApi? Api, string? Problem)> ConnectAsync(CancellationToken cancellationToken)
    {
        var (serviceBase, problem) = await FindReachableServiceAsync(cancellationToken);
        if (serviceBase is null)
        {
            return (null, problem);
        }

        if (_api is null || _apiBase != serviceBase)
        {
            _api = await DetectApiAsync(serviceBase, cancellationToken);
            _apiBase = serviceBase;
        }

        return (_api, null);
    }

    /// <summary>
    /// Tells the server kinds apart by routes only each has. Foundry Local 1.x+ answers <c>/models/loaded</c> with a
    /// JSON array and <c>/status</c> with its model cache path; 0.x answers <c>/openai/loadedmodels</c> or
    /// <c>/openai/status</c>. Two signals per version, so one failing route cannot make Foundry Local look like a
    /// generic server (which would skip the loaded-model check). Anything else is a generic OpenAI-compatible server.
    /// </summary>
    private async Task<IModelManagementApi> DetectApiAsync(Uri serviceBase, CancellationToken cancellationToken)
    {
        var loadTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.Local.ModelLoadTimeoutSeconds));

        var status = await LocalHttp.GetAsync(_http, serviceBase, "/status", ProbeTimeout, cancellationToken);
        if ((status.IsSuccess && status.Body.Contains("modelCachePath", StringComparison.OrdinalIgnoreCase))
            || IsModelArray(await LocalHttp.GetAsync(_http, serviceBase, "/models/loaded", ProbeTimeout, cancellationToken)))
        {
            return new FoundryServerApi(_http, serviceBase, _cli, loadTimeout);
        }

        if ((await LocalHttp.GetAsync(_http, serviceBase, "/openai/status", ProbeTimeout, cancellationToken)).IsSuccess
            || IsModelArray(await LocalHttp.GetAsync(_http, serviceBase, "/openai/loadedmodels", ProbeTimeout, cancellationToken)))
        {
            return new FoundryServiceApi(_http, serviceBase, loadTimeout);
        }

        return new OpenAICompatibleApi(_http, serviceBase);
    }

    // Both Foundry Local versions answer their loaded-model route with a JSON array; a catch-all or error page does not.
    private static bool IsModelArray(HttpGetResult result) => result.IsSuccess && result.Body.StartsWith('[');

    /// <summary>
    /// Loads the active model, then confirms it: a successful load response is not proof, the model counts as loaded
    /// only once it is in the server's loaded list (checked a few times, as the list can lag the response).
    /// </summary>
    private async Task<(IReadOnlyList<string> Loaded, string? Problem)> LoadAndConfirmAsync(IModelManagementApi api, CancellationToken cancellationToken)
    {
        var loadProblem = await api.LoadAsync(_activeModelId, cancellationToken);
        if (loadProblem is not null)
        {
            return (Array.Empty<string>(), loadProblem);
        }

        IReadOnlyList<string> loaded = Array.Empty<string>();
        for (int attempt = 0; attempt < LoadConfirmAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(LoadConfirmDelay, cancellationToken);

            loaded = (await api.ListLoadedAsync(cancellationToken)).Ids ?? Array.Empty<string>();
            if (api.IsLoaded(loaded, _activeModelId))
            {
                return (loaded, null);
            }
        }

        return (loaded, $"{api.DisplayName} accepted the request to load '{_activeModelId}', but it is not among the loaded models. {DescribeLoaded(api, loaded)} " +
                        $"Try loading it in a terminal with 'foundry model run {_activeModelId}' to see Foundry Local's error.");
    }

    /// <summary>What the server reports as loaded, so a mismatch (e.g. another model loaded) is visible to the user.</summary>
    private string DescribeLoaded(IModelManagementApi api, IReadOnlyList<string> loaded) =>
        loaded.Count == 0
            ? $"{api.DisplayName} reports no loaded models; the app is set to '{_activeModelId}'."
            : $"{api.DisplayName} reports loaded: {string.Join(", ", loaded)}; the app is set to '{_activeModelId}'.";

    /// <summary>
    /// Chooses the model: the user's choice; else the best preferred model that is loaded; else the best preferred
    /// model that is downloaded (it is loaded before use); else the configured <c>Local.ModelId</c>. The result is
    /// normalized to the name the server's load route accepts.
    /// A weak model that happens to be loaded never outranks a preferred one on disk: loading takes a minute once,
    /// while a sub-1B model gives poor summaries every time.
    /// </summary>
    private async Task SelectModelAsync(IModelManagementApi api, IReadOnlyList<string>? loaded, CancellationToken cancellationToken)
    {
        string chosen;
        if (_userSelectedModelId is not null)
        {
            chosen = _userSelectedModelId;
        }
        else if (!_options.Local.AutoSelectModel)
        {
            chosen = _options.LocalModelId;
        }
        else
        {
            var preferences = _options.Local.GetPreferredModels();
            var fromLoaded = loaded is { Count: > 0 } ? LocalModelSelector.Select(loaded, preferences, configuredModelId: string.Empty) : string.Empty;
            if (fromLoaded.Length > 0)
            {
                chosen = fromLoaded;
            }
            else
            {
                var downloaded = (await api.ListDownloadedAsync(cancellationToken)).Ids ?? Array.Empty<string>();
                chosen = LocalModelSelector.Select(downloaded, preferences, _options.LocalModelId);
            }
        }

        _activeModelId = await api.NormalizeAsync(chosen, cancellationToken);
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

    /// <summary>
    /// Works out where the service should be: the CLI's status (newer "foundry server" first, then the older
    /// "foundry service"), then (optionally) starting it, then configuration.
    /// </summary>
    private async Task<Uri> DiscoverAsync(bool discover, List<string> notes, CancellationToken cancellationToken)
    {
        if (discover)
        {
            string? workingNoun = null;
            foreach (var noun in new[] { "server", "service" })
            {
                var status = await _cli.RunAsync($"{noun} status", TimeSpan.FromSeconds(15), cancellationToken);
                if (FoundryCli.ParseServiceUri(status.Output) is { } found) return found;

                if (status.Problem is not null && status.Output.Length == 0)
                {
                    notes.Add(status.Problem);                   // CLI not installed or failed to run
                    break;
                }

                if (status.Succeeded)
                {
                    workingNoun = noun;                          // the CLI knows this command; the service is stopped
                    break;
                }
            }

            if (workingNoun is not null && _options.Local.AutoStartService)
            {
                var start = await _cli.RunAsync($"{workingNoun} start", TimeSpan.FromSeconds(90), cancellationToken);
                var started = FoundryCli.ParseServiceUri(start.Output)
                              ?? FoundryCli.ParseServiceUri((await _cli.RunAsync($"{workingNoun} status", TimeSpan.FromSeconds(15), cancellationToken)).Output);
                if (started is not null) return started;
                notes.Add(start.Problem is null
                    ? $"Foundry Local did not report an address after 'foundry {workingNoun} start'."
                    : $"Starting Foundry Local failed: {start.Problem}");
            }
            else if (workingNoun is not null)
            {
                notes.Add($"Foundry Local is not running. Start it with 'foundry {workingNoun} start'.");
            }
        }

        // Explicit configuration (or the legacy ~/.foundry/daemon.json lookup) when discovery found nothing.
        var configured = new Uri(_options.GetEffectiveLocalEndpoint());
        return new Uri($"{configured.Scheme}://{configured.Authority}");
    }

    /// <summary>Any HTTP response means a server is listening; only connection failures and timeouts count as down.</summary>
    /// <returns>Null when reachable; otherwise the reason.</returns>
    private async Task<string?> ProbeAsync(Uri serviceBase, CancellationToken cancellationToken)
    {
        var result = await LocalHttp.GetAsync(_http, serviceBase, "/status", ProbeTimeout, cancellationToken);
        return result.Status is not null
            ? null
            : $"No local model service is reachable at {serviceBase} ({result.Error}).";
    }

    private IChatClient GetOrCreateClient(Uri serviceBase, string chatModelId)
    {
        var key = $"{serviceBase}|{chatModelId}";
        if (_client is not null && _clientKey == key) return _client;

        _client?.Dispose();
        _client = CreateChatClient(new Uri(serviceBase, "v1"), chatModelId, TimeSpan.FromSeconds(Math.Max(1, _options.Local.TimeoutSeconds)), _chatTransport);
        _clientKey = key;
        return _client;
    }

    private static bool IsChatModel(string id) =>
        !NonChatMarkers.Any(marker => id.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string StripVersion(string id)
    {
        int colon = id.LastIndexOf(':');
        return colon > 0 && colon < id.Length - 1 && id.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') < 0
            ? id[..colon]
            : id;
    }

    private LocalModelStatus Unavailable(string problem) => new(false, Endpoint, _activeModelId, problem, null);

    private static bool IsAuto(string endpoint) => string.Equals(endpoint, "auto", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }
}
