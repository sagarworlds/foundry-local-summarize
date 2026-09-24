namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Chooses which locally served model to use from an ordered preference list. Pure logic with no I/O,
/// so the choice is deterministic and unit-testable.
/// </summary>
public static class LocalModelSelector
{
    /// <summary>
    /// Built-in preference order, strongest instruction-following first. All are Foundry Local aliases
    /// that run on consumer hardware; the 0.5B default is deliberately absent because it cannot reliably
    /// follow the persona templates without inventing content.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPreferences = new[]
    {
        "phi-4-mini",
        "qwen2.5-7b",
        "phi-3.5-mini",
        "qwen2.5-1.5b"
    };

    // Variants that share a preferred prefix but are unsuited to summarization: reasoning models emit
    // long <think> traces that consume the output budget, and coder models are tuned for code.
    private static readonly string[] ExcludedMarkers = { "reasoning", "coder" };

    /// <summary>
    /// Selects the model id to use.
    /// </summary>
    /// <param name="availableModelIds">Model ids the endpoint reports (loaded or cached).</param>
    /// <param name="preferences">Aliases in priority order; each matches an id exactly or as a prefix.</param>
    /// <param name="configuredModelId">The explicitly configured model, used when no preference matches.</param>
    /// <returns>
    /// The best available preferred model; otherwise <paramref name="configuredModelId"/>. The configured id is
    /// returned even when it is not in the list, because an incomplete listing must not override explicit config.
    /// </returns>
    public static string Select(IEnumerable<string> availableModelIds, IReadOnlyList<string> preferences, string configuredModelId)
    {
        ArgumentNullException.ThrowIfNull(availableModelIds);
        ArgumentNullException.ThrowIfNull(preferences);

        var available = availableModelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var preference in preferences.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var best = available
                .Where(id => Matches(id, preference))
                .OrderBy(HardwareRank)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (best != null)
            {
                return best;
            }
        }

        return configuredModelId;
    }

    private static bool Matches(string modelId, string preference)
    {
        bool prefixMatch = modelId.Equals(preference, StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith(preference + "-", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith(preference + ":", StringComparison.OrdinalIgnoreCase); // Ollama tags, e.g. "phi-4-mini:latest"

        return prefixMatch && !ExcludedMarkers.Any(marker =>
            modelId.Contains(marker, StringComparison.OrdinalIgnoreCase) &&
            !preference.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Prefers accelerated builds of the same model: GPU, then NPU, then CPU.</summary>
    private static int HardwareRank(string modelId)
    {
        if (modelId.Contains("gpu", StringComparison.OrdinalIgnoreCase) || modelId.Contains("cuda", StringComparison.OrdinalIgnoreCase)) return 0;
        if (modelId.Contains("npu", StringComparison.OrdinalIgnoreCase)) return 1;
        if (modelId.Contains("cpu", StringComparison.OrdinalIgnoreCase)) return 3;
        return 2;
    }
}
