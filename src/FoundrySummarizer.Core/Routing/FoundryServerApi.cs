using System.Net.Http;
using System.Text.Json;

namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Foundry Local 1.x and later ("foundry server"). Model management lives at <c>/models/loaded</c>,
/// <c>/models/load/{alias}</c> and <c>/models/unload/{alias}</c>; load and unload take the alias ("phi-4-mini"),
/// while chat requests need the full id of the loaded variant ("Phi-4-mini-instruct-generic-gpu:5"), which
/// <c>/models/loaded</c> reports. Downloaded models are read from the CLI (<c>foundry model list</c>), because this
/// version's <c>/v1/models</c> lists the whole catalog.
/// </summary>
public sealed class FoundryServerApi : IModelManagementApi
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _serviceBase;
    private readonly IFoundryCli _cli;
    private readonly TimeSpan _loadTimeout;
    private IReadOnlyList<FoundryModelRow> _catalog = Array.Empty<FoundryModelRow>();

    /// <param name="http">Client without a timeout of its own.</param>
    /// <param name="serviceBase">Scheme and authority of the server.</param>
    /// <param name="cli">Runs <c>foundry model list</c>.</param>
    /// <param name="loadTimeout">Maximum wait for a model load.</param>
    public FoundryServerApi(HttpClient http, Uri serviceBase, IFoundryCli cli, TimeSpan loadTimeout)
    {
        _http = http;
        _serviceBase = serviceBase;
        _cli = cli;
        _loadTimeout = loadTimeout;
    }

    /// <inheritdoc />
    public string DisplayName => $"Foundry Local at {_serviceBase}";

    /// <inheritdoc />
    public bool SupportsLoading => true;

    /// <inheritdoc />
    public async Task<ModelIdListing> ListDownloadedAsync(CancellationToken cancellationToken)
    {
        var problem = await RefreshCatalogAsync(cancellationToken);
        if (problem is not null) return new ModelIdListing(null, problem);

        var chat = _catalog.Where(row => row.Type.Length == 0 || row.Type.Equals("Chat", StringComparison.OrdinalIgnoreCase)).ToList();
        if (chat.Any(row => row.IsCached is not null))
        {
            return new ModelIdListing(chat.Where(row => row.IsCached == true).Select(row => row.Alias).ToList(), null);
        }

        // The Cached column was unreadable: 'foundry cache list' lists only downloaded models.
        var cacheList = FoundryModelTable.Parse((await _cli.RunAsync("cache list", TimeSpan.FromSeconds(60), cancellationToken)).Output);
        if (cacheList.Count > 0)
        {
            var aliases = _catalog.Select(row => row.Alias).ToList();
            var cached = cacheList.Select(row => FoundryModelTable.AliasOf(row.Alias, aliases) ?? row.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ModelIdListing(chat.Where(row => cached.Contains(row.Alias)).Select(row => row.Alias).ToList(), null);
        }

        // Last resort: offer every chat model; loading one that is not downloaded fails with a clear message.
        return new ModelIdListing(chat.Select(row => row.Alias).ToList(), null);
    }

    /// <inheritdoc />
    public async Task<ModelIdListing> ListLoadedAsync(CancellationToken cancellationToken)
    {
        var result = await LocalHttp.GetAsync(_http, _serviceBase, "/models/loaded", ListTimeout, cancellationToken);
        if (!result.IsSuccess) return new ModelIdListing(null, $"/models/loaded: {result.Describe()}");

        try
        {
            return new ModelIdListing(LocalModelCatalog.ParseModelIds(result.Body), null);
        }
        catch (JsonException ex)
        {
            return new ModelIdListing(null, $"/models/loaded returned unreadable JSON: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<string> NormalizeAsync(string name, CancellationToken cancellationToken)
    {
        if (_catalog.Count == 0) await RefreshCatalogAsync(cancellationToken);
        var aliases = _catalog.Select(row => row.Alias).ToList();
        return aliases.FirstOrDefault(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? FoundryModelTable.AliasOf(name, aliases)
               ?? name;
    }

    /// <inheritdoc />
    public bool IsLoaded(IReadOnlyList<string> loaded, string name) => LoadedIdFor(loaded, name) is not null;

    /// <inheritdoc />
    public string ChatModelId(IReadOnlyList<string> loaded, string name) => LoadedIdFor(loaded, name) ?? name;

    /// <inheritdoc />
    public async Task<string?> LoadAsync(string name, CancellationToken cancellationToken)
    {
        var result = await LocalHttp.GetAsync(_http, _serviceBase, $"/models/load/{LocalHttp.PathSegment(name)}", _loadTimeout, cancellationToken);
        if (result.IsSuccess) return null;

        return result.Status switch
        {
            400 when result.Body.Contains("not cached", StringComparison.OrdinalIgnoreCase) =>
                $"Model '{name}' is not downloaded. Download it with 'foundry model download {name}'.",
            404 => $"Foundry Local has no model named '{name}'. Check the name with 'foundry model list'.",
            null => $"Loading '{name}' failed: {result.Describe()}. Loading can take minutes; raise Foundry:Local:ModelLoadTimeoutSeconds if needed.",
            _ => $"Foundry Local could not load '{name}' ({result.Describe()}). Try 'foundry model run {name}' in a terminal to see the full error."
        };
    }

    /// <inheritdoc />
    public async Task<string?> UnloadAsync(string name, CancellationToken cancellationToken)
    {
        var result = await LocalHttp.GetAsync(_http, _serviceBase, $"/models/unload/{LocalHttp.PathSegment(name)}", TimeSpan.FromSeconds(60), cancellationToken);
        return result.IsSuccess || result.Status == 404
            ? null
            : $"Foundry Local could not unload '{name}' ({result.Describe()}); it stays in memory until its idle timeout.";
    }

    /// <summary>The loaded id (e.g. "…-gpu:5") that belongs to alias <paramref name="name"/>, or null.</summary>
    private string? LoadedIdFor(IReadOnlyList<string> loaded, string name)
    {
        var aliases = _catalog.Select(row => row.Alias).Append(name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return loaded.FirstOrDefault(id =>
            id.Equals(name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(FoundryModelTable.AliasOf(id, aliases), name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> RefreshCatalogAsync(CancellationToken cancellationToken)
    {
        var result = await _cli.RunAsync("model list", TimeSpan.FromSeconds(90), cancellationToken);
        var rows = FoundryModelTable.Parse(result.Output);
        if (rows.Count == 0)
        {
            return $"Could not read the model list from 'foundry model list'{(result.Problem is null ? "" : $" ({result.Problem})")}.";
        }

        _catalog = rows;
        return null;
    }
}
