using Microsoft.Extensions.AI;
using FoundrySummarizer.Benchmark;
using FoundrySummarizer.Core.Benchmarking;
using FoundrySummarizer.Core.Evaluation;
using FoundrySummarizer.Core.Grounding;
using FoundrySummarizer.Core.Personas;

namespace FoundrySummarizer.Tests;

public class BenchmarkTests
{
    private static readonly BenchmarkDocument Proposal = new("proposal.txt",
        "Project Helios proposal by David Chen. The hardware quote is $150,000, due on August 28, 2026.");

    private static BenchmarkRunner CreateRunner(Func<string, IChatClient> clients) =>
        new(clients, new PromptyEngine(), new VectorGroundingService(), new EvaluationPipeline());

    /// <summary>Answers every request with fixed text; throws when <paramref name="text"/> is null.</summary>
    private sealed class FixedChatClient(string? text) : IChatClient
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (text is null) throw new HttpRequestException("Connection refused\nat 127.0.0.1");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    private const string GroundedSummary = """
        ### 1. Executive Summary & Strategic Value
        - Project Helios, proposed by David Chen, needs a $150,000 hardware budget.
        ### 2. Financial & Cost Assessment
        - Hardware quote: $150,000.
        ### 3. Key Milestones & Critical Path
        - Due August 28, 2026.
        ### 4. Strategic Risks & Recommended Executive Actions
        - Approve the hardware budget.
        """;

    private const string InventedSummary = """
        ### 1. Executive Summary & Strategic Value
        - Olivia Park approved $900,000 on March 3, 2031.
        """;

    [Fact]
    public async Task Runner_RanksGroundedModelAboveInventingModelAndRecordsFailures()
    {
        var clients = new Dictionary<string, FixedChatClient>
        {
            ["grounded"] = new(GroundedSummary),
            ["inventing"] = new(InventedSummary),
            ["offline"] = new(null)
        };
        var progress = new List<string>();

        var report = await CreateRunner(id => clients[id]).RunAsync(
            new[] { "offline", "inventing", "grounded" },
            new[] { Proposal },
            new[] { "Executive Bullets" },
            runsPerCase: 2,
            progress: new SyncProgress(progress.Add));

        Assert.Equal(6, report.Results.Count);
        Assert.Equal(6, progress.Count);
        Assert.Equal(new[] { "grounded", "inventing", "offline" }, report.Scorecards.Select(s => s.ModelId));

        var grounded = report.Scorecards[0];
        Assert.Equal(1.0, grounded.MeanGrounding);
        Assert.Equal(0, grounded.NeedsReviewCount);

        var inventing = report.Scorecards[1];
        Assert.Equal(0.0, inventing.MeanGrounding);
        Assert.Equal(2, inventing.NeedsReviewCount);
        Assert.Contains(report.Results.First(r => r.ModelId == "inventing").UnverifiedClaims, c => c.Contains("Olivia Park"));

        var offline = report.Scorecards[2];
        Assert.Equal(2, offline.Failures);
        Assert.All(report.Results.Where(r => r.ModelId == "offline"), r =>
        {
            Assert.False(r.Succeeded);
            Assert.Equal("HttpRequestException: Connection refused at 127.0.0.1", r.Error); // collapsed to one line
        });

        Assert.All(clients.Values, c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task Runner_RejectsUnknownPersonaBeforeCallingAnyModel()
    {
        var client = new FixedChatClient(GroundedSummary);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateRunner(_ => client).RunAsync(new[] { "m" }, new[] { Proposal }, new[] { "Poet Laureate" }));

        Assert.Contains("Poet Laureate", ex.Message);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void ReportWriter_ProducesScorecardClaimsErrorsAndQuotedCsv()
    {
        var report = new BenchmarkReport(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero), new[]
        {
            new BenchmarkRunResult("model-a", "doc, one.txt", "Executive Bullets", 1, 0.5, 0.8, 0.9, true, "NEEDS_REVIEW",
                TimeSpan.FromSeconds(2), false, new[] { "Unverified amount \"$9\": amount not found in the source." },
                "Line one\nLine \"two\"", null),
            new BenchmarkRunResult("model-b", "doc.txt", "Executive Bullets", 1, 0, 0, 0, true, "ERROR",
                TimeSpan.FromSeconds(1), false, Array.Empty<string>(), string.Empty, "HttpRequestException: refused")
        });

        var markdown = BenchmarkReportWriter.ToMarkdown(report, "baseline");
        Assert.Contains("| model-a | 50% | 50% | 80% | 90% | 1/1 | 0/1 | 2.0s |", markdown);
        Assert.Contains("**Label:** baseline", markdown);
        Assert.Contains("Unverified amount", markdown);
        Assert.Contains("model-b · doc.txt · Executive Bullets · run 1: HttpRequestException: refused", markdown);

        var csv = BenchmarkReportWriter.ToCsv(report);
        Assert.Contains("\"doc, one.txt\"", csv);
        Assert.Contains("\"Line one\nLine \"\"two\"\"\"", csv);

        var history = BenchmarkReportWriter.ToHistoryRows(report, "baseline").ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal(BenchmarkReportWriter.HistoryHeader.Split(',').Length, history[0].Split(',').Length);
        Assert.StartsWith("2026-09-24T09:00:00+00:00,model-a,1,0,0.5,", history[0]);
    }

    [Fact]
    public void CliOptions_ParsesListsAndDefaults()
    {
        var options = BenchmarkCliOptions.Parse(new[] { "--models", "phi-4-mini, qwen2.5-7b", "--runs", "3", "--label", "new prompt" });

        Assert.Equal(new[] { "phi-4-mini", "qwen2.5-7b" }, options.Models);
        Assert.Equal(3, options.Runs);
        Assert.Equal("new prompt", options.Label);
        Assert.Equal("samples", options.SamplesDirectory);
        Assert.Empty(options.Personas);
        Assert.Equal(300, options.TimeoutSeconds);
    }

    [Theory]
    [InlineData("--runs", "0")]
    [InlineData("--timeout", "abc")]
    [InlineData("--modles", "x")]
    [InlineData("stray")]
    [InlineData("--models")]
    public void CliOptions_RejectsInvalidInput(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => BenchmarkCliOptions.Parse(args));
    }

    private sealed class SyncProgress(Action<string> onReport) : IProgress<string>
    {
        public void Report(string value) => onReport(value);
    }
}
