using System.Net.Http;
using System.Text.Json;

namespace FoundrySummarizer.Core.Routing;

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
    public async Task<IReadOnlyList<string>> GetLoadedModelIdsAsync(Uri baseUri, CancellationToken cancellationToken = default)
    {
        var url = $"{baseUri.Scheme}://{baseUri.Authority}/openai/loadedmodels";
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Expected on Ollama and other OpenAI-compatible servers that lack this Foundry-specific route.
                return Array.Empty<string>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseModelIds(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Discovery is an optimisation: on failure the caller falls back to /v1/models or the configured id.
            System.Diagnostics.Debug.WriteLine($"[LocalModelCatalog] Loaded-model discovery failed at {url}: {ex.Message}");
            return Array.Empty<string>();
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
