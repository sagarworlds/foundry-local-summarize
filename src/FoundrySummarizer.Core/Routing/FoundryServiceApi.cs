using System.Net.Http;
using System.Text.Json;

namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Foundry Local 0.x ("foundry service"): model management at <c>/openai/loadedmodels</c>, <c>/openai/models</c>,
/// <c>/openai/load/{id}</c> and <c>/openai/unload/{id}</c>. Names are resolved through the catalog at
/// <c>/foundry/list</c> to the exact versioned id those routes accept, following Microsoft's Foundry Local SDK 0.8.
/// </summary>
public sealed class FoundryServiceApi : IModelManagementApi
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _serviceBase;
    private readonly TimeSpan _loadTimeout;
    private FoundryCatalog? _catalog;

    /// <param name="http">Client without a timeout of its own.</param>
    /// <param name="serviceBase">Scheme and authority of the service.</param>
    /// <param name="loadTimeout">Maximum wait for a model load.</param>
    public FoundryServiceApi(HttpClient http, Uri serviceBase, TimeSpan loadTimeout)
    {
        _http = http;
        _serviceBase = serviceBase;
        _loadTimeout = loadTimeout;
    }

    /// <inheritdoc />
    public string DisplayName => $"Foundry Local at {_serviceBase}";

    /// <inheritdoc />
    public bool SupportsLoading => true;

    /// <inheritdoc />
    public async Task<ModelIdListing> ListDownloadedAsync(CancellationToken cancellationToken)
    {
        // Re-read the catalog so a model downloaded since the last refresh resolves too.
        _catalog = await ReadCatalogAsync(cancellationToken);
        var listing = await ListAsync("/openai/models", cancellationToken);
        if (listing.Ids is null) return listing;

        // Shown by catalog id (the only form the load route accepts), once each even when spelled differently.
        return new ModelIdListing(listing.Ids
            .Select(id => _catalog?.Resolve(id)?.Id ?? id)
            .DistinctBy(StripVersion, StringComparer.OrdinalIgnoreCase)
            .ToList(), null);
    }

    /// <inheritdoc />
    public Task<ModelIdListing> ListLoadedAsync(CancellationToken cancellationToken) =>
        ListAsync("/openai/loadedmodels", cancellationToken);

    /// <inheritdoc />
    public async Task<string> NormalizeAsync(string name, CancellationToken cancellationToken)
    {
        _catalog ??= await ReadCatalogAsync(cancellationToken);
        return _catalog?.Resolve(name)?.Id ?? name;
    }

    /// <inheritdoc />
    public bool IsLoaded(IReadOnlyList<string> loaded, string name) =>
        FoundryLocalService.IsInLoadedList(loaded, name, _catalog);

    /// <inheritdoc />
    public string ChatModelId(IReadOnlyList<string> loaded, string name) => name;

    /// <inheritdoc />
    public async Task<string?> LoadAsync(string name, CancellationToken cancellationToken)
    {
        _catalog ??= await ReadCatalogAsync(cancellationToken);
        var entry = _catalog?.Resolve(name);
        if (_catalog is { Count: > 0 } && entry is null)
        {
            return $"Foundry Local's catalog has no model named '{name}'. Pick another model, or check the name with 'foundry model list'.";
        }

        var loadId = entry?.Id ?? name;
        var query = $"timeout={Math.Max(1, (int)_loadTimeout.TotalSeconds)}";
        if (entry is not null && _catalog!.ExecutionProviderOverride(entry) is { } ep)
        {
            query += $"&ep={ep}";
        }

        var result = await LocalHttp.GetAsync(_http, _serviceBase, $"/openai/load/{LocalHttp.PathSegment(loadId)}?{query}", _loadTimeout, cancellationToken);
        if (result.IsSuccess) return null;

        return result.Status is null
            ? $"Loading model '{loadId}' failed: {result.Describe()}. Load it first with 'foundry model run {loadId}'."
            : $"Foundry Local could not load model '{loadId}' ({result.Describe()}). " +
              $"Check that it is downloaded with 'foundry cache list', or download it with 'foundry model download {loadId}'.";
    }

    /// <inheritdoc />
    public async Task<string?> UnloadAsync(string name, CancellationToken cancellationToken)
    {
        var id = await NormalizeAsync(name, cancellationToken);
        var result = await LocalHttp.GetAsync(_http, _serviceBase, $"/openai/unload/{LocalHttp.PathSegment(id)}?force=true", TimeSpan.FromSeconds(60), cancellationToken);
        // 404: the model was not loaded; nothing to free.
        return result.IsSuccess || result.Status == 404
            ? null
            : $"Foundry Local could not unload '{id}' ({result.Describe()}); it stays in memory until its idle timeout.";
    }

    private async Task<ModelIdListing> ListAsync(string route, CancellationToken cancellationToken)
    {
        var result = await LocalHttp.GetAsync(_http, _serviceBase, route, ListTimeout, cancellationToken);
        if (!result.IsSuccess) return new ModelIdListing(null, $"{route}: {result.Describe()}");

        try
        {
            return new ModelIdListing(LocalModelCatalog.ParseModelIds(result.Body), null);
        }
        catch (JsonException ex)
        {
            return new ModelIdListing(null, $"{route} returned unreadable JSON: {ex.Message}");
        }
    }

    /// <returns>The catalog, or null when it cannot be read (ids are then used as listed).</returns>
    private async Task<FoundryCatalog?> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var result = await LocalHttp.GetAsync(_http, _serviceBase, "/foundry/list", ListTimeout, cancellationToken);
        if (!result.IsSuccess) return _catalog;

        try
        {
            return FoundryCatalog.Parse(result.Body);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FoundryServiceApi] Unreadable catalog: {ex.Message}");
            return _catalog;
        }
    }

    private static string StripVersion(string id)
    {
        int colon = id.LastIndexOf(':');
        return colon > 0 && colon < id.Length - 1 && id.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') < 0 ? id[..colon] : id;
    }
}
