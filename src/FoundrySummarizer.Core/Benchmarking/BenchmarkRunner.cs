using System.Diagnostics;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Evaluation;
using FoundrySummarizer.Core.Grounding;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;

namespace FoundrySummarizer.Core.Benchmarking;

/// <summary>
/// Runs the app's summarization pipeline (policy grounding, multi-part summarizing, evaluation) over a set of
/// documents, personas and models, so models and prompt changes can be compared on the same inputs.
/// </summary>
public class BenchmarkRunner
{
    private readonly Func<string, IChatClient> _clientFactory;
    private readonly IPromptyEngine _promptyEngine;
    private readonly IVectorGroundingService _groundingService;
    private readonly IEvaluationPipeline _evaluationPipeline;
    private readonly SummarizationConfig _summarizationConfig;

    /// <param name="clientFactory">
    /// Creates a chat client for a model id. Use a direct client, not the hybrid router: the router's demo
    /// fallback would turn an unreachable model into canned output instead of a visible failure.
    /// </param>
    /// <param name="promptyEngine">Source of persona prompts.</param>
    /// <param name="groundingService">Policy grounding, as used by the app.</param>
    /// <param name="evaluationPipeline">Scores each summary.</param>
    /// <param name="summarizationConfig">Token budgets for long documents.</param>
    public BenchmarkRunner(
        Func<string, IChatClient> clientFactory,
        IPromptyEngine promptyEngine,
        IVectorGroundingService groundingService,
        IEvaluationPipeline evaluationPipeline,
        SummarizationConfig? summarizationConfig = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _promptyEngine = promptyEngine ?? throw new ArgumentNullException(nameof(promptyEngine));
        _groundingService = groundingService ?? throw new ArgumentNullException(nameof(groundingService));
        _evaluationPipeline = evaluationPipeline ?? throw new ArgumentNullException(nameof(evaluationPipeline));
        _summarizationConfig = summarizationConfig ?? new SummarizationConfig();
    }

    /// <summary>Summarizes and evaluates every document × persona × run on every model.</summary>
    /// <param name="modelIds">Models to compare.</param>
    /// <param name="documents">Documents to summarize.</param>
    /// <param name="personaNames">Personas to use; each must be registered in the prompty engine.</param>
    /// <param name="runsPerCase">Repeats per case, to expose run-to-run variation.</param>
    /// <param name="progress">Receives one line per case as it completes.</param>
    /// <param name="cancellationToken">Stops the benchmark.</param>
    /// <returns>All results. A failed case is recorded with its error; the benchmark carries on.</returns>
    /// <exception cref="ArgumentException">An input list is empty, a persona is unknown, or runs is below 1.</exception>
    public async Task<BenchmarkReport> RunAsync(
        IReadOnlyList<string> modelIds,
        IReadOnlyList<BenchmarkDocument> documents,
        IReadOnlyList<string> personaNames,
        int runsPerCase = 1,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (modelIds is null || modelIds.Count == 0) throw new ArgumentException("At least one model is required.", nameof(modelIds));
        if (documents is null || documents.Count == 0) throw new ArgumentException("At least one document is required.", nameof(documents));
        if (runsPerCase < 1) throw new ArgumentException("Runs per case must be at least 1.", nameof(runsPerCase));

        var personas = ResolvePersonas(personaNames);
        var startedAt = DateTimeOffset.Now;
        var results = new List<BenchmarkRunResult>();

        // Policy grounding depends only on the document, so it is computed once per document.
        var groundingByDocument = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            groundingByDocument[document.Name] = await _groundingService.BuildGroundingPromptContextAsync(document.Text, cancellationToken);
        }

        int total = modelIds.Count * documents.Count * personas.Count * runsPerCase;
        foreach (var modelId in modelIds)
        {
            using var client = _clientFactory(modelId);
            var summarizer = new MultiPartSummarizer(client, _promptyEngine, _summarizationConfig);

            foreach (var document in documents)
            foreach (var persona in personas)
            for (int run = 1; run <= runsPerCase; run++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await RunCaseAsync(summarizer, modelId, document, persona, groundingByDocument[document.Name], run, cancellationToken);
                results.Add(result);

                progress?.Report($"[{results.Count}/{total}] {modelId} | {document.Name} | {persona.Name} | run {run}: " +
                    (result.Succeeded ? $"grounding {result.Grounding * 100:0}%, {result.Status}, {result.Duration.TotalSeconds:F1}s" : $"FAILED: {result.Error}"));
            }
        }

        return new BenchmarkReport(startedAt, results);
    }

    private async Task<BenchmarkRunResult> RunCaseAsync(
        IDocumentSummarizer summarizer,
        string modelId,
        BenchmarkDocument document,
        PromptyDocument persona,
        string groundingContext,
        int run,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var summary = await summarizer.SummarizeAsync(
                new SummarizationRequest(persona, document.Text, groundingContext),
                cancellationToken: cancellationToken);
            stopwatch.Stop();

            var evaluation = await _evaluationPipeline.EvaluateSummaryAsync(
                document.Name,
                persona.Name,
                document.Text,
                summary.Summary,
                persona.BuildReferenceText(groundingContext),
                cancellationToken);

            return new BenchmarkRunResult(
                modelId, document.Name, persona.Name, run,
                evaluation.GroundingScore, evaluation.CompletenessScore, evaluation.PersonaAdherenceScore,
                evaluation.ContentSafetyPassed, evaluation.OverallStatus, stopwatch.Elapsed, summary.UsedMultiPart,
                evaluation.DiagnosticNotes, summary.Summary, Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Recorded rather than thrown so one unreachable model or failed call does not discard the other
            // results; the report lists every failure with its message.
            return new BenchmarkRunResult(
                modelId, document.Name, persona.Name, run,
                0, 0, 0, SafetyPassed: true, Status: "ERROR", stopwatch.Elapsed, UsedMultiPart: false,
                Array.Empty<string>(), string.Empty, Error: $"{ex.GetType().Name}: {OneLine(ex.Message)}");
        }
    }

    /// <summary>Collapses multi-line server errors so they fit one progress line and one table cell.</summary>
    private static string OneLine(string message) =>
        string.Join(" ", message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private List<PromptyDocument> ResolvePersonas(IReadOnlyList<string> personaNames)
    {
        if (personaNames is null || personaNames.Count == 0)
        {
            throw new ArgumentException("At least one persona is required.", nameof(personaNames));
        }

        var unknown = personaNames.Where(n => _promptyEngine.GetPersona(n) is null).ToList();
        if (unknown.Count > 0)
        {
            var known = string.Join(", ", _promptyEngine.AvailablePersonas.Select(p => $"'{p.Name}'"));
            throw new ArgumentException($"Unknown persona(s): {string.Join(", ", unknown)}. Available: {known}.", nameof(personaNames));
        }

        return personaNames.Select(n => _promptyEngine.GetPersona(n)!).ToList();
    }
}
