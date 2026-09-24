using System.Text.Json;

namespace FoundrySummarizer.Core.Routing;

/// <summary>Result of listing model ids from one route.</summary>
/// <param name="Ids">The ids, or null when the route could not be read.</param>
/// <param name="Error">Why the route could not be read; null on success.</param>
public record ModelIdListing(IReadOnlyList<string>? Ids, string? Error);

/// <summary>Reads the model lists returned by local model servers (Foundry Local and OpenAI-compatible servers).</summary>
public static class ModelListParser
{
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
