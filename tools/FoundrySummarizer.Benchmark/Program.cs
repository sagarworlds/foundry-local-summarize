using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using FoundrySummarizer.Benchmark;
using FoundrySummarizer.Core.Benchmarking;
using FoundrySummarizer.Core.Evaluation;
using FoundrySummarizer.Core.Grounding;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;

// Exit codes: 0 = at least one case succeeded, 1 = every case failed, 2 = invalid arguments or setup error.
BenchmarkCliOptions cli;
try
{
    cli = BenchmarkCliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (cli.ShowHelp)
{
    Console.WriteLine(BenchmarkCliOptions.Usage);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
    Console.Error.WriteLine("Cancelling after the current request...");
};

try
{
    var foundry = LoadFoundryOptions();
    if (cli.Endpoint is not null)
    {
        // An explicit --endpoint wins over discovery.
        foundry.Local.Endpoint = cli.Endpoint;
        foundry.Local.AutoDiscover = false;
    }

    // Same discovery as the app: `foundry service status` (starting the service if configured), then config.
    using var localService = new FoundryLocalService(foundry);
    var status = await localService.CheckAsync(ensureModelLoaded: false, cancellation.Token);
    if (!status.IsAvailable || status.Endpoint is null)
    {
        Console.Error.WriteLine($"Cannot reach a local model service: {status.Problem}");
        return 2;
    }

    var endpoint = status.Endpoint;
    var serviceBase = new Uri($"{endpoint.Scheme}://{endpoint.Authority}");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var catalog = new LocalModelCatalog(http);
    var loadedModels = await catalog.TryGetModelIdsAsync(serviceBase, "/openai/loadedmodels", cancellation.Token);
    var listedModels = await catalog.TryGetModelIdsAsync(serviceBase, "/openai/models", cancellation.Token)
                       ?? await catalog.TryGetModelIdsAsync(serviceBase, "/v1/models", cancellation.Token)
                       ?? Array.Empty<string>();

    var models = ResolveModels(cli, foundry, loadedModels ?? Array.Empty<string>(), listedModels);

    // Foundry Local serves only loaded models; load each one up front so load time is not counted as
    // summary time and a model that cannot load is reported once, clearly.
    if (loadedModels is not null)
    {
        foreach (var model in models.Where(m => !loadedModels.Contains(m, StringComparer.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"Loading {model}...");
            var loadProblem = await localService.LoadModelAsync(serviceBase, model, cancellation.Token);
            if (loadProblem is not null) Console.Error.WriteLine($"  {loadProblem}");
        }
    }

    var documents = await LoadDocumentsAsync(cli.SamplesDirectory, cancellation.Token);
    var promptyEngine = new PromptyEngine();
    var personas = cli.Personas.Count > 0 ? cli.Personas : promptyEngine.AvailablePersonas.Select(p => p.Name).ToList();
    var unknownPersonas = personas.Where(p => promptyEngine.GetPersona(p) is null).ToList();
    if (unknownPersonas.Count > 0)
    {
        Console.Error.WriteLine($"Unknown persona(s): {string.Join(", ", unknownPersonas)}. Available: {string.Join(", ", promptyEngine.AvailablePersonas.Select(p => $"\"{p.Name}\""))}.");
        return 2;
    }

    Console.WriteLine($"Endpoint:  {endpoint}");
    Console.WriteLine($"Models:    {string.Join(", ", models)}");
    Console.WriteLine($"Documents: {string.Join(", ", documents.Select(d => d.Name))}");
    Console.WriteLine($"Personas:  {string.Join(", ", personas)}");
    Console.WriteLine($"Runs:      {cli.Runs} per case ({models.Count * documents.Count * personas.Count * cli.Runs} summaries)");
    Console.WriteLine();

    var runner = new BenchmarkRunner(
        modelId => CreateDirectClient(endpoint, modelId, cli.TimeoutSeconds),
        promptyEngine,
        new VectorGroundingService(),
        new EvaluationPipeline(),
        foundry.Summarization);

    var report = await runner.RunAsync(models, documents, personas, cli.Runs, new ConsoleProgress(), cancellation.Token);

    var written = await WriteReportsAsync(report, cli, cancellation.Token);
    Console.WriteLine();
    Console.WriteLine(BenchmarkReportWriter.ToMarkdown(report, cli.Label).Split("## Results by case")[0].TrimEnd());
    Console.WriteLine();
    foreach (var path in written) Console.WriteLine($"Wrote {path}");

    return report.Results.Any(r => r.Succeeded) ? 0 : 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Benchmark cancelled; no report written.");
    return 2;
}
catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static FoundryOptions LoadFoundryOptions()
{
    // Same appsettings.json the desktop app uses, read from the current directory (normally the repo root).
    var configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var options = new FoundryOptions();
    configuration.GetSection(FoundryOptions.SectionName).Bind(options);
    return options;
}

// Resolves --models aliases to served ids; with no --models, benchmarks every loaded model.
static IReadOnlyList<string> ResolveModels(
    BenchmarkCliOptions cli, FoundryOptions foundry, IReadOnlyList<string> loaded, IReadOnlyList<string> listedModels)
{
    var available = loaded.Concat(listedModels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    if (cli.Models.Count > 0)
    {
        // "phi-4-mini" → "phi-4-mini-instruct-generic-gpu" when the endpoint serves it; unknown ids are kept
        // as typed so the server's own error message ends up in the report.
        return cli.Models
            .Select(alias => LocalModelSelector.Select(available, new[] { alias }, alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Speech and embedding models share the listing but cannot summarize.
    var candidates = loaded
        .Where(id => !id.Contains("whisper", StringComparison.OrdinalIgnoreCase) && !id.Contains("embed", StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (candidates.Count > 0) return candidates;

    Console.WriteLine($"No loaded models reported; using Foundry:Local:ModelId '{foundry.LocalModelId}'. Pass --models to choose.");
    return new[] { foundry.LocalModelId };
}

static async Task<IReadOnlyList<BenchmarkDocument>> LoadDocumentsAsync(string directory, CancellationToken cancellationToken)
{
    if (!Directory.Exists(directory))
    {
        throw new DirectoryNotFoundException($"Samples folder '{Path.GetFullPath(directory)}' does not exist. Pass --samples.");
    }

    var pipeline = new DocumentIngestionPipeline();

    // Audio "transcription" here is metadata only, so audio files would benchmark nothing meaningful.
    var audio = new AudioTranscriptionService().SupportedExtensions;
    var supported = pipeline.SupportedExtensions.Except(audio, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

    var documents = new List<BenchmarkDocument>();
    foreach (var file in Directory.EnumerateFiles(directory).Where(f => supported.Contains(Path.GetExtension(f))).Order(StringComparer.Ordinal))
    {
        var ingested = await pipeline.IngestFileAsync(file, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ingested.ExtractedText))
        {
            documents.Add(new BenchmarkDocument(Path.GetFileName(file), ingested.ExtractedText));
        }
    }

    if (documents.Count == 0)
    {
        throw new InvalidOperationException($"No readable documents ({string.Join(", ", supported.Order())}) found in '{Path.GetFullPath(directory)}'.");
    }

    return documents;
}

static IChatClient CreateDirectClient(Uri endpoint, string modelId, int timeoutSeconds)
{
    var client = new OpenAIClient(
        new ApiKeyCredential("local-foundry-key"),
        new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds) });
    return client.GetChatClient(modelId).AsIChatClient();
}

static async Task<IReadOnlyList<string>> WriteReportsAsync(BenchmarkReport report, BenchmarkCliOptions cli, CancellationToken cancellationToken)
{
    Directory.CreateDirectory(cli.OutputDirectory);
    var stem = Path.Combine(cli.OutputDirectory, $"benchmark-{report.StartedAt:yyyyMMdd-HHmmss}");
    var markdownPath = stem + ".md";
    var csvPath = stem + ".csv";
    var historyPath = Path.Combine(cli.OutputDirectory, "history.csv");

    await File.WriteAllTextAsync(markdownPath, BenchmarkReportWriter.ToMarkdown(report, cli.Label), cancellationToken);
    await File.WriteAllTextAsync(csvPath, BenchmarkReportWriter.ToCsv(report), cancellationToken);

    var historyRows = BenchmarkReportWriter.ToHistoryRows(report, cli.Label).ToList();
    if (!File.Exists(historyPath))
    {
        historyRows.Insert(0, BenchmarkReportWriter.HistoryHeader);
    }
    await File.AppendAllLinesAsync(historyPath, historyRows, cancellationToken);

    return new[] { markdownPath, csvPath, historyPath };
}

/// <summary>Writes progress lines synchronously, so they appear in order as each case finishes.</summary>
internal sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}
