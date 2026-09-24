using System.Net.Http;
using System.Text.Json;

namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// A generic OpenAI-compatible server (e.g. Ollama) that loads models on demand: models come from
/// <c>/v1/models</c>, and there is nothing to load, unload or confirm.
/// </summary>
public sealed class OpenAICompatibleApi : IModelManagementApi
{
    private readonly HttpClient _http;
    private readonly Uri _serviceBase;

    /// <param name="http">Client without a timeout of its own.</param>
    /// <param name="serviceBase">Scheme and authority of the server.</param>
    public OpenAICompatibleApi(HttpClient http, Uri serviceBase)
    {
        _http = http;
        _serviceBase = serviceBase;
    }

    /// <inheritdoc />
    public string DisplayName => $"the model server at {_serviceBase}";

    /// <inheritdoc />
    public bool SupportsLoading => false;

    /// <inheritdoc />
    public async Task<ModelIdListing> ListDownloadedAsync(CancellationToken cancellationToken)
    {
        var result = await LocalHttp.GetAsync(_http, _serviceBase, "/v1/models", TimeSpan.FromSeconds(30), cancellationToken);
        if (!result.IsSuccess) return new ModelIdListing(null, $"/v1/models: {result.Describe()}");

        try
        {
            return new ModelIdListing(LocalModelCatalog.ParseModelIds(result.Body), null);
        }
        catch (JsonException ex)
        {
            return new ModelIdListing(null, $"/v1/models returned unreadable JSON: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<ModelIdListing> ListLoadedAsync(CancellationToken cancellationToken) => ListDownloadedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<string> NormalizeAsync(string name, CancellationToken cancellationToken) => Task.FromResult(name);

    /// <inheritdoc />
    public bool IsLoaded(IReadOnlyList<string> loaded, string name) => true; // loaded on first request

    /// <inheritdoc />
    public string ChatModelId(IReadOnlyList<string> loaded, string name) => name;

    /// <inheritdoc />
    public Task<string?> LoadAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<string?> UnloadAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
