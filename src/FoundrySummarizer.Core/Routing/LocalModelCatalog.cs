using System.Net.Http;
using System.Text.Json;

namespace FoundrySummarizer.Core.Routing;

/// <summary>Result of listing model ids from one route.</summary>
/// <param name="Ids">The ids, or null when the route could not be read.</param>
/// <param name="Error">Why the route could not be read; null on success.</param>
public record ModelIdListing(IReadOnlyList<string>? Ids, string? Error);

/// <summary>
/// Discovers which models a local OpenAI-compatible endpoint (Foundry Local or Ollama) can serve.
/// </summary>
public class LocalModelCatalog
{
    private readonly HttpClient _httpClient;

    /// <param name="httpClient">Client used for discovery calls; its lifetime is owned by the caller.</param>
    public LocalModelCatalog(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Returns the models currently loaded in Foundry Local memory (<c>/openai/loadedmodels</c>). Preferring
    /// loaded models avoids selecting one that is only cached on disk and would fail or stall on first use.
    /// </summary>
    /// <param name="baseUri">Any URI on the local service; only scheme and authority are used.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Loaded model ids, or an empty list when the endpoint is not Foundry Local or is unreachable.</returns>
    public async Task<IReadOnlyList<string>> GetLoadedModelIdsAsync(Uri baseUri, CancellationToken cancellationToken = default) =>
        await TryGetModelIdsAsync(baseUri, "/openai/loadedmodels", cancellationToken) ?? Array.Empty<string>();

    /// <summary>
    /// Lists models from <paramref name="route"/> (e.g. <c>/openai/loadedmodels</c>, <c>/openai/models</c>, <c>/v1/models</c>).
    /// </summary>
    /// <returns>
    /// The ids, an empty list when the route exists but lists nothing, or null when the route is not served
    /// (Ollama lacks the Foundry routes) or cannot be read. Null lets callers tell "not Foundry" from "none loaded".
    /// </returns>
    public async Task<IReadOnlyList<string>?> TryGetModelIdsAsync(Uri baseUri, string route, CancellationToken cancellationToken = default) =>
        (await ListModelIdsAsync(baseUri, route, cancellationToken)).Ids;

    /// <summary>Like <see cref="TryGetModelIdsAsync"/>, but says why the listing failed.</summary>
    /// <returns>The ids, or null ids plus the reason (HTTP status, connection error or unreadable body).</returns>
    public async Task<ModelIdListing> ListModelIdsAsync(Uri baseUri, string route, CancellationToken cancellationToken = default)
    {
        var url = $"{baseUri.Scheme}://{baseUri.Authority}{route}";
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelIdListing(null, $"{url} answered HTTP {(int)response.StatusCode}");
            }

            return new ModelIdListing(ParseModelIds(await response.Content.ReadAsStringAsync(cancellationToken)), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Discovery is an optimisation: on failure the caller falls back to another route or the configured id.
            System.Diagnostics.Debug.WriteLine($"[LocalModelCatalog] Model listing failed at {url}: {ex.Message}");
            return new ModelIdListing(null, $"{url} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts model ids from a model-listing payload. Accepts the OpenAI shape
    /// (<c>{"data":[{"id":"..."}]}</c>) and Foundry's plain array (<c>["id", ...]</c> or objects with id/name).
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <returns>The ids found, or an empty list for empty or unrecognised payloads.</returns>
    /// <exception cref="JsonException">The payload is not valid JSON.</exception>
    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data,
            _ => default
        };

        if (items.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var ids = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            string? id = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String => idProp.GetString(),
                JsonValueKind.Object when item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String => nameProp.GetString(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }

        return ids;
    }
}
