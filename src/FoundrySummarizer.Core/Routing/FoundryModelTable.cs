namespace FoundrySummarizer.Core.Routing;

/// <summary>One row of the <c>foundry model list</c> table.</summary>
/// <param name="Alias">Model name as the CLI shows it, e.g. "phi-4-mini".</param>
/// <param name="Type">"Chat", "Speech", "Embedding", "Multimodal"…</param>
/// <param name="IsCached">True when the Cached column shows ● (downloaded); null when the table has no readable Cached column.</param>
public record FoundryModelRow(string Alias, string Type, bool? IsCached);

/// <summary>
/// Reads the table printed by <c>foundry model list</c> (Foundry Local 1.x+). That version's web service lists every
/// catalog model at /v1/models, downloaded or not, so the CLI's Cached column is the only record of which models
/// are on this machine. Columns are found by their header names, so reordering or new columns do not break parsing.
/// </summary>
public static class FoundryModelTable
{
    private static readonly char[] Borders = { '│', '┃', '|' };

    // Colour codes some CLIs emit even when their output is redirected.
    private static readonly System.Text.RegularExpressions.Regex AnsiEscape = new(@"\x1B\[[0-9;]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Parses the CLI output.</summary>
    /// <returns>The rows; empty when the output contains no recognisable table.</returns>
    public static IReadOnlyList<FoundryModelRow> Parse(string output)
    {
        var rows = new List<FoundryModelRow>();
        int nameColumn = -1, typeColumn = -1, cachedColumn = -1;

        foreach (var line in AnsiEscape.Replace(output ?? string.Empty, string.Empty).Split('\n'))
        {
            if (line.IndexOfAny(Borders) < 0) continue;
            var cells = line.Split(Borders).Select(c => c.Trim()).ToArray();

            if (nameColumn < 0)
            {
                nameColumn = Array.FindIndex(cells, c => c.Equals("Model Name", StringComparison.OrdinalIgnoreCase) || c.Equals("Alias", StringComparison.OrdinalIgnoreCase));
                typeColumn = Array.FindIndex(cells, c => c.Equals("Type", StringComparison.OrdinalIgnoreCase) || c.Equals("Task", StringComparison.OrdinalIgnoreCase));
                cachedColumn = Array.FindIndex(cells, c => c.Equals("Cached", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (nameColumn >= cells.Length || cells[nameColumn].Length == 0) continue;

            var type = typeColumn >= 0 && typeColumn < cells.Length ? cells[typeColumn] : string.Empty;
            rows.Add(new FoundryModelRow(cells[nameColumn], type, ReadCached(cachedColumn >= 0 && cachedColumn < cells.Length ? cells[cachedColumn] : null)));
        }

        return rows;
    }

    /// <summary>Reads a Cached cell: ● / yes / true is cached, ○ / no / false / empty is not; anything else is unknown.</summary>
    private static bool? ReadCached(string? cell) => cell switch
    {
        null => null,
        _ when cell.Contains('●') || cell.Equals("yes", StringComparison.OrdinalIgnoreCase) || cell.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        _ when cell.Contains('○') || cell.Length == 0 || cell.Equals("no", StringComparison.OrdinalIgnoreCase) || cell.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ => null
    };

    /// <summary>
    /// The alias a full model id belongs to, e.g. "Phi-4-mini-instruct-generic-gpu:5" → "phi-4-mini". The longest
    /// matching alias wins, so "phi-4-mini-reasoning-…" is not taken for "phi-4-mini".
    /// </summary>
    /// <returns>The alias, or null when none matches.</returns>
    public static string? AliasOf(string modelId, IEnumerable<string> aliases) =>
        aliases
            .Where(alias => modelId.Equals(alias, StringComparison.OrdinalIgnoreCase)
                            || modelId.StartsWith(alias + "-", StringComparison.OrdinalIgnoreCase)
                            || modelId.StartsWith(alias + ":", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(alias => alias.Length)
            .FirstOrDefault();
}
