using System.Globalization;

namespace FoundrySummarizer.Benchmark;

/// <summary>Command-line options for the benchmark tool.</summary>
public sealed class BenchmarkCliOptions
{
    public const string Usage = """
        Summary quality benchmark: summarizes sample documents with each persona on each model and
        fact-checks the results. Talks to the model endpoint directly (no offline demo fallback).

        Usage: dotnet run --project tools/FoundrySummarizer.Benchmark -- [options]

          --models <a,b>     Models to compare (ids or aliases such as phi-4-mini). Default: every model
                             Foundry Local has loaded, else Foundry:Local:ModelId from appsettings.json.
          --endpoint <url>   OpenAI-compatible endpoint. Default: from appsettings.json / ~/.foundry/daemon.json.
          --samples <dir>    Folder of documents (.txt, .docx, .pptx, .pdf). Default: ./samples
          --personas <a,b>   Persona names. Default: all built-in personas.
          --runs <n>         Repeats per case, to expose run-to-run variation. Default: 1
          --timeout <sec>    Per-request timeout. Default: 300
          --out <dir>        Output folder for the reports and history.csv. Default: ./benchmark-results
          --label <text>     Note stored with the results, e.g. "new legal prompt".
          --help             Show this help.
        """;

    public IReadOnlyList<string> Models { get; private init; } = Array.Empty<string>();
    public string? Endpoint { get; private init; }
    public string SamplesDirectory { get; private init; } = "samples";
    public IReadOnlyList<string> Personas { get; private init; } = Array.Empty<string>();
    public int Runs { get; private init; } = 1;
    public int TimeoutSeconds { get; private init; } = 300;
    public string OutputDirectory { get; private init; } = "benchmark-results";
    public string? Label { get; private init; }
    public bool ShowHelp { get; private init; }

    /// <summary>Parses command-line arguments.</summary>
    /// <exception cref="ArgumentException">An option is unknown, missing its value, or has an invalid value.</exception>
    public static BenchmarkCliOptions Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool help = false;

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "/?")
            {
                help = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'. Run with --help for usage.");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{arg}' needs a value.");
            }

            values[arg[2..]] = args[++i];
        }

        var known = new[] { "models", "endpoint", "samples", "personas", "runs", "timeout", "out", "label" };
        var unknown = values.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}. Run with --help for usage.");
        }

        return new BenchmarkCliOptions
        {
            ShowHelp = help,
            Models = SplitList(values.GetValueOrDefault("models")),
            Endpoint = values.GetValueOrDefault("endpoint"),
            SamplesDirectory = values.GetValueOrDefault("samples") ?? "samples",
            Personas = SplitList(values.GetValueOrDefault("personas")),
            Runs = PositiveInt(values, "runs", 1),
            TimeoutSeconds = PositiveInt(values, "timeout", 300),
            OutputDirectory = values.GetValueOrDefault("out") ?? "benchmark-results",
            Label = values.GetValueOrDefault("label")
        };
    }

    private static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int PositiveInt(IReadOnlyDictionary<string, string> values, string name, int fallback)
    {
        if (!values.TryGetValue(name, out var raw)) return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0) return parsed;
        throw new ArgumentException($"--{name} must be a positive whole number (got '{raw}').");
    }
}
