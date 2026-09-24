using System.Globalization;
using System.Text;

namespace FoundrySummarizer.Core.Benchmarking;

/// <summary>Formats a <see cref="BenchmarkReport"/> as Markdown (for reading) and CSV (for spreadsheets and history).</summary>
public static class BenchmarkReportWriter
{
    /// <summary>Column header for <see cref="ToHistoryRows"/>; the CLI appends rows under it across sessions.</summary>
    public const string HistoryHeader = "timestamp,model,runs,failures,mean_grounding,min_grounding,mean_completeness,mean_persona_adherence,needs_review,mean_seconds,label";

    // Enough to show a model's typical mistakes without flooding the report.
    private const int MaxClaimsPerCase = 5;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Human-readable report: scorecards, per-case results, unverified claims and errors.</summary>
    /// <param name="report">The benchmark results.</param>
    /// <param name="label">Optional note describing what was tested, e.g. "new legal prompt".</param>
    public static string ToMarkdown(BenchmarkReport report, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();

        sb.AppendLine($"# Summary Quality Benchmark — {report.StartedAt:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(label)) sb.AppendLine().AppendLine($"**Label:** {label}");
        sb.AppendLine();
        sb.AppendLine("Grounding = share of the summary's figures, dates and names that the source supports. ");
        sb.AppendLine("Below 70% a summary is marked NEEDS_REVIEW. Means exclude failed runs.");
        sb.AppendLine();

        sb.AppendLine("## Scorecard");
        sb.AppendLine();
        sb.AppendLine("| Model | Mean grounding | Worst grounding | Completeness | Persona format | Needs review | Failed | Avg time |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in report.Scorecards)
        {
            sb.AppendLine($"| {s.ModelId} | {Pct(s.MeanGrounding)} | {Pct(s.MinGrounding)} | {Pct(s.MeanCompleteness)} | {Pct(s.MeanPersonaAdherence)} | " +
                $"{s.NeedsReviewCount}/{s.Runs - s.Failures} | {s.Failures}/{s.Runs} | {s.MeanDuration.TotalSeconds:F1}s |");
        }

        sb.AppendLine();
        sb.AppendLine("## Results by case");
        sb.AppendLine();
        sb.AppendLine("| Model | Document | Persona | Run | Grounding | Completeness | Status | Time | Multi-part |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in report.Results)
        {
            sb.AppendLine($"| {r.ModelId} | {Cell(r.DocumentName)} | {r.PersonaName} | {r.Run} | " +
                (r.Succeeded ? $"{Pct(r.Grounding)} | {Pct(r.Completeness)} | {r.Status}" : "— | — | ERROR") +
                $" | {r.Duration.TotalSeconds:F1}s | {(r.UsedMultiPart ? "yes" : "no")} |");
        }

        var withClaims = report.Results.Where(r => r.Succeeded && r.UnverifiedClaims.Count > 0).ToList();
        if (withClaims.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Unverified claims");
            foreach (var r in withClaims)
            {
                sb.AppendLine();
                sb.AppendLine($"**{r.ModelId} · {r.DocumentName} · {r.PersonaName} · run {r.Run}**");
                foreach (var claim in r.UnverifiedClaims.Take(MaxClaimsPerCase)) sb.AppendLine($"- {claim}");
                if (r.UnverifiedClaims.Count > MaxClaimsPerCase) sb.AppendLine($"- …and {r.UnverifiedClaims.Count - MaxClaimsPerCase} more");
            }
        }

        var failures = report.Results.Where(r => !r.Succeeded).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Errors");
            sb.AppendLine();
            foreach (var r in failures) sb.AppendLine($"- {r.ModelId} · {r.DocumentName} · {r.PersonaName} · run {r.Run}: {r.Error}");
        }

        return sb.ToString();
    }

    /// <summary>One CSV row per case, including the summary text, for detailed analysis.</summary>
    public static string ToCsv(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("model,document,persona,run,grounding,completeness,persona_adherence,safety_passed,status,seconds,multi_part,unverified_claims,error,summary");
        foreach (var r in report.Results)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.ModelId), Csv(r.DocumentName), Csv(r.PersonaName), r.Run.ToString(Invariant),
                Num(r.Grounding), Num(r.Completeness), Num(r.PersonaAdherence), r.SafetyPassed ? "true" : "false",
                Csv(r.Status), r.Duration.TotalSeconds.ToString("F1", Invariant), r.UsedMultiPart ? "true" : "false",
                Csv(string.Join(" | ", r.UnverifiedClaims)), Csv(r.Error ?? string.Empty), Csv(r.Summary)));
        }
        return sb.ToString();
    }

    /// <summary>One row per model (without header), for appending to a long-running history file.</summary>
    /// <param name="report">The benchmark results.</param>
    /// <param name="label">Optional note stored with each row.</param>
    public static IEnumerable<string> ToHistoryRows(BenchmarkReport report, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var timestamp = report.StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", Invariant);
        return report.Scorecards.Select(s => string.Join(",",
            timestamp, Csv(s.ModelId), s.Runs.ToString(Invariant), s.Failures.ToString(Invariant),
            Num(s.MeanGrounding), Num(s.MinGrounding), Num(s.MeanCompleteness), Num(s.MeanPersonaAdherence),
            s.NeedsReviewCount.ToString(Invariant), s.MeanDuration.TotalSeconds.ToString("F1", Invariant), Csv(label ?? string.Empty)));
    }

    private static string Pct(double value) => (value * 100).ToString("0", Invariant) + "%";

    private static string Num(double value) => value.ToString("0.###", Invariant);

    private static string Cell(string text) => text.Replace("|", "\\|");

    /// <summary>RFC 4180 quoting: fields with commas, quotes or line breaks are quoted and quotes doubled.</summary>
    private static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
