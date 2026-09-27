using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Reads a model's context window from its files. Foundry Local runs models with ONNX Runtime GenAI, whose
/// <c>genai_config.json</c> (next to each downloaded model) gives the context length the model was built for
/// (<c>model.context_length</c>) and the most tokens the runtime will process in one request
/// (<c>search.max_length</c>); the smaller of the two is what a request can use.
/// </summary>
public static class ModelContextReader
{
    private const string ConfigFileName = "genai_config.json";

    // Model folders carry the id with its version as a suffix ("…-gpu-5", "…-gpu:5", "…-gpu_v5"), or no version.
    private static readonly Regex VersionSuffix = new(@"^(?:[-_:.]v?(?<version>\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds the model's <c>genai_config.json</c> under <paramref name="cacheDirectory"/> and reads its context window.
    /// </summary>
    /// <param name="cacheDirectory">Foundry Local's model cache folder.</param>
    /// <param name="modelId">The model id, with or without its version, e.g. "Phi-4-mini-instruct-generic-gpu:5".</param>
    /// <returns>The context window in tokens, or null when no readable configuration was found for the model.</returns>
    public static int? FindContextTokens(string? cacheDirectory, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || string.IsNullOrWhiteSpace(modelId) || !Directory.Exists(cacheDirectory)) return null;

        var (name, version) = SplitVersion(modelId);
        IEnumerable<string> configs;
        try
        {
            configs = Directory.EnumerateFiles(cacheDirectory, ConfigFileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                IgnoreInaccessible = true
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[ModelContextReader] Cannot search {cacheDirectory}: {ex.Message}");
            return null;
        }

        var matches = configs
            .Select(path => (Path: path, Folder: MatchingFolder(cacheDirectory, path, name)))
            .Where(m => m.Folder is not null)
            .ToList();
        if (matches.Count == 0) return null;

        // Several downloaded versions: prefer the one requested; otherwise the smallest window is the safe choice.
        var sameVersion = version is null ? new() : matches.Where(m => m.Folder!.Value.Version == version).ToList();
        var candidates = sameVersion.Count > 0 ? sameVersion : matches;
        var windows = candidates.Select(m => ReadContextTokens(m.Path)).OfType<int>().ToList();
        return windows.Count == 0 ? null : windows.Min();
    }

    /// <summary>Reads one <c>genai_config.json</c>.</summary>
    /// <returns>The smaller of <c>model.context_length</c> and <c>search.max_length</c>, or null when neither is readable.</returns>
    public static int? ReadContextTokens(string configPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var limits = new[] { Positive(doc.RootElement, "model", "context_length"), Positive(doc.RootElement, "search", "max_length") }
                .OfType<int>().ToList();
            return limits.Count == 0 ? null : limits.Min();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[ModelContextReader] Cannot read {configPath}: {ex.Message}");
            return null;
        }
    }

    private static int? Positive(JsonElement root, string section, string property) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object
        && s.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) && value > 0
            ? value
            : null;

    /// <summary>
    /// The folder on the path below the cache that is named after the model, optionally followed by a version
    /// ("…-gpu-5" but not "…-gpu-reasoning"), with that version; null when there is none.
    /// </summary>
    private static (string Folder, string? Version)? MatchingFolder(string cacheDirectory, string configPath, string name)
    {
        var relative = Path.GetRelativePath(cacheDirectory, Path.GetDirectoryName(configPath)!);
        foreach (var folder in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (!folder.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = VersionSuffix.Match(folder[name.Length..]);
            if (suffix.Success) return (folder, suffix.Groups["version"].Success ? suffix.Groups["version"].Value : null);
        }

        return null;
    }

    /// <summary>"Phi-4-mini-instruct-generic-gpu:5" → ("Phi-4-mini-instruct-generic-gpu", "5").</summary>
    private static (string Name, string? Version) SplitVersion(string modelId)
    {
        int colon = modelId.LastIndexOf(':');
        return colon > 0 && colon < modelId.Length - 1 && modelId[(colon + 1)..].All(char.IsDigit)
            ? (modelId[..colon], modelId[(colon + 1)..])
            : (modelId, null);
    }
}
