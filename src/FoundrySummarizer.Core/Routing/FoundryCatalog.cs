using System.Text.Json;

namespace FoundrySummarizer.Core.Routing;

/// <summary>One entry of Foundry Local's model catalog (<c>/foundry/list</c>).</summary>
/// <param name="Id">Full model id including its version, e.g. "Phi-4-mini-instruct-generic-gpu:5".</param>
/// <param name="Alias">Short name, e.g. "phi-4-mini"; empty when the catalog gives none.</param>
/// <param name="ExecutionProvider">Runtime the build targets, e.g. "CUDAExecutionProvider" or "WebGpuExecutionProvider".</param>
public record FoundryCatalogModel(string Id, string Alias, string ExecutionProvider);

/// <summary>
/// Foundry Local's model catalog, used to turn the names the service lists (or the user types) into the exact
/// versioned ids its model-management routes accept. <c>/openai/load/{id}</c> answers 404 for anything else, e.g.
/// "Phi-4-mini-instruct-generic-gpu" instead of "Phi-4-mini-instruct-generic-gpu:5". The matching rules mirror
/// Microsoft's Foundry Local SDK (0.8): exact id, then highest version of "&lt;id&gt;:", then alias.
/// </summary>
public sealed class FoundryCatalog
{
    private readonly IReadOnlyList<FoundryCatalogModel> _models;
    private readonly bool _hasCuda;

    /// <param name="models">Catalog entries in the service's order (best device first).</param>
    public FoundryCatalog(IReadOnlyList<FoundryCatalogModel> models)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _hasCuda = _models.Any(m => string.Equals(m.ExecutionProvider, "CUDAExecutionProvider", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Number of catalog entries.</summary>
    public int Count => _models.Count;

    /// <summary>Finds the catalog entry for an id (with or without version) or an alias.</summary>
    /// <returns>The entry, or null when the catalog does not know the name.</returns>
    public FoundryCatalogModel? Resolve(string idOrAlias)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias)) return null;

        var exact = _models.FirstOrDefault(m => m.Id.Equals(idOrAlias, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var prefix = idOrAlias + ":";
        var newest = _models
            .Where(m => m.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .MaxBy(m => Version(m.Id));
        if (newest is not null) return newest;

        return _models.FirstOrDefault(m => m.Alias.Equals(idOrAlias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The execution provider to request when loading, or null for the model's default. Generic-GPU builds run on
    /// CUDA when the machine has it, as the official SDK requests.
    /// </summary>
    public string? ExecutionProviderOverride(FoundryCatalogModel model) =>
        _hasCuda && model.Id.Contains("-generic-gpu:", StringComparison.OrdinalIgnoreCase) ? "cuda" : null;

    /// <summary>Parses a <c>/foundry/list</c> response body.</summary>
    /// <exception cref="JsonException">The body is not valid JSON.</exception>
    public static FoundryCatalog Parse(string json)
    {
        var models = new List<FoundryCatalogModel>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new FoundryCatalog(models);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || String(item, "name") is not { Length: > 0 } id) continue;

            var provider = item.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object
                ? String(runtime, "executionProvider") ?? string.Empty
                : string.Empty;
            models.Add(new FoundryCatalogModel(id, String(item, "alias") ?? string.Empty, provider));
        }

        return new FoundryCatalog(models);
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Version(string id)
    {
        var colon = id.LastIndexOf(':');
        return colon >= 0 && int.TryParse(id.AsSpan(colon + 1), out var version) ? version : -1;
    }
}
