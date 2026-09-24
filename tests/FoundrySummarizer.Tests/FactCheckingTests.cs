using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using FoundrySummarizer.Core.Evaluation;
using FoundrySummarizer.Core.Evaluation.Evaluators;
using FoundrySummarizer.Core.Evaluation.FactChecking;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

public class FactCheckingTests
{
    private const string Transcript = """
        MEETING: Project Helios review on August 28, 2026.
        Participants: Marcus Vance (Vice President of Technology), David Chen (Lead Systems Architect), Elena Rostova (Legal Counsel).
        David: the hardware quote is $150,000 and Phase 1 is 100% complete.
        Elena: the supplier wants a 3x liability cap; policy allows 1x. Termination needs thirty (30) days notice.
        Marcus: David submits the request by Friday. Elena returns the contract by Tuesday. Target is Q3 2026.
        """;

    private static IReadOnlyList<CheckedFact> Unsupported(string summary, string? reference = null) =>
        FactChecker.Check(summary, Transcript, reference).Unsupported;

    [Theory]
    [InlineData("The quote is $150,000.")]
    [InlineData("The quote is $150k.")]
    [InlineData("The quote is $0.15M.")]
    [InlineData("The quote is 150,000 USD.")]
    public void Amounts_MatchAcrossFormats(string summary)
    {
        var report = FactChecker.Check(summary, Transcript);

        Assert.Equal(FactKind.Amount, Assert.Single(report.Facts).Fact.Kind);
        Assert.Equal(1.0, report.Score);
    }

    [Fact]
    public void Figures_NotInSourceAreFlagged()
    {
        var unsupported = Unsupported("Quote: $950,000, 80% complete, a 5x cap and 45 days notice.");

        Assert.Equal(
            new[] { FactKind.Amount, FactKind.Percentage, FactKind.Multiplier, FactKind.Duration },
            unsupported.Select(f => f.Fact.Kind).OrderBy(k => k));
    }

    [Fact]
    public void Figures_InSourceAreSupported()
    {
        var report = FactChecker.Check("Phase 1 is 100% complete; the 3x cap exceeds the 1x policy; 30 days notice.", Transcript);

        Assert.All(report.Facts, f => Assert.True(f.Supported, f.Fact.Text));
        Assert.Equal(4, report.Facts.Count);
    }

    [Theory]
    [InlineData("Reviewed on August 28, 2026.", true)]
    [InlineData("Reviewed on 28 August 2026.", true)]
    [InlineData("Reviewed on Aug 28.", true)]            // less specific than the source is fine
    [InlineData("Reviewed on 2026-08-28.", true)]
    [InlineData("Reviewed on 08/28/2026.", true)]
    [InlineData("Target is Q3.", true)]
    [InlineData("Reviewed on August 29, 2026.", false)]
    [InlineData("Reviewed on August 28, 2027.", false)]
    [InlineData("Target is Q4 2026.", false)]
    public void Dates_AreComparedByComponent(string summary, bool expectedSupported)
    {
        var fact = Assert.Single(FactChecker.Check(summary, Transcript).Facts);

        Assert.Equal(FactKind.Date, fact.Fact.Kind);
        Assert.Equal(expectedSupported, fact.Supported);
    }

    [Fact]
    public void Dates_AmbiguousNumericFormAcceptsEitherReading()
    {
        // 04/05/2026 may be April 5 or 4 May; the source says 4 May 2026.
        var report = FactChecker.Check("Due 04/05/2026.", "Delivery is due 4 May 2026.");

        Assert.True(Assert.Single(report.Facts).Supported);
    }

    [Fact]
    public void Names_InventedSurnameIsFlaggedEvenWithRealFirstName()
    {
        var unsupported = Unsupported("The request was approved by David Miller.");

        var fact = Assert.Single(unsupported);
        Assert.Equal("David Miller", fact.Fact.Text);
        Assert.Contains("'Miller'", fact.MissingDetail);
    }

    [Fact]
    public void Names_AcronymOfSpelledOutTitleIsSupported()
    {
        Assert.Empty(Unsupported("Approval is needed from the VP before Friday."));
    }

    [Fact]
    public void Names_HeadingsTableHeadersAndLineLabelsAreNotChecked()
    {
        var summary = """
            ### 1. Executive Summary & Strategic Value
            | # | Task Description | Assignee / Owner |
            |---|------------------|------------------|
            - **Core Strategic Objective**: modernize the platform.
            - 🚩 **Attention Required**: renegotiate the cap.
            """;

        Assert.Empty(FactChecker.Check(summary, Transcript).Facts);
    }

    [Fact]
    public void Names_OwnerColumnCellsAreAlwaysChecked()
    {
        var summary = """
            | # | Task | Owner |
            |---|------|-------|
            | 1 | Submit request | David |
            | 2 | Update forecast | Sarah |
            | 3 | Review terms | Unassigned |
            """;

        var unsupported = Unsupported(summary);

        Assert.Equal("Sarah", Assert.Single(unsupported).Fact.Text);
    }

    [Fact]
    public void Names_SentenceInitialWordIsIgnoredButRestOfPhraseChecked()
    {
        // "Pending" is capitalised only because it starts the sentence; "EMEA" is an invented term.
        var unsupported = Unsupported("Pending approval for the EMEA rollout.");

        Assert.Equal("EMEA", Assert.Single(unsupported).Fact.Text);
    }

    [Fact]
    public void ReferenceMaterial_AllowsQuotingPolicyFacts()
    {
        const string summary = "Spend above $100,000 needs sign-off under Policy FIN-202.";
        const string policy = "Policy FIN-202: expenditures exceeding $100,000 require approval.";

        Assert.NotEmpty(Unsupported(summary));
        Assert.Empty(Unsupported(summary, policy));
    }

    [Fact]
    public void OfflineDemoOutput_ScoresZero()
    {
        var report = FactChecker.Check(HybridChatClientRouter.FallbackNotice + "The quote is $150,000.", Transcript);

        Assert.True(report.IsOfflineDemoOutput);
        Assert.Equal(0.0, report.Score);
    }

    [Fact]
    public void RepeatedMentionsCountOnce()
    {
        var report = FactChecker.Check("$150,000 requested. The $150,000 quote needs approval.", Transcript);

        Assert.Single(report.Facts);
    }

    [Fact]
    public async Task GroundingEvaluator_ListsEachUnverifiedClaimAsWarning()
    {
        var evaluator = new GroundingEvaluator();
        var messages = new List<ChatMessage> { new(ChatRole.User, Transcript) };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "David Chen owns the $150,000 request, due Friday. Olivia Park approved a $40,000 top-up."));

        var result = await evaluator.EvaluateAsync(messages, response);
        var metric = (NumericMetric)result.Metrics[GroundingEvaluator.MetricName];

        // Supported: "David Chen", "$150,000", "Friday". Unsupported: "Olivia Park", "$40,000".
        Assert.Equal(0.6, metric.Value);
        Assert.True(metric.Interpretation?.Failed);
        Assert.Contains("Verified 3 of 5 facts", metric.Reason);
        Assert.NotNull(metric.Diagnostics);
        Assert.Contains(metric.Diagnostics, d => d.Severity == EvaluationDiagnosticSeverity.Warning && d.Message.Contains("Olivia Park"));
        Assert.Contains(metric.Diagnostics, d => d.Message.Contains("$40,000"));
    }

    [Fact]
    public async Task GroundingEvaluator_UsesReferenceMaterialContext()
    {
        var evaluator = new GroundingEvaluator();
        var messages = new List<ChatMessage> { new(ChatRole.User, "The quote is $150,000.") };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "The $150,000 quote exceeds the $100,000 limit."));

        var withoutPolicy = await evaluator.EvaluateAsync(messages, response);
        var withPolicy = await evaluator.EvaluateAsync(messages, response,
            additionalContext: new[] { new ReferenceMaterialContext("Spend over $100,000 needs VP approval.") });

        Assert.Equal(0.5, ((NumericMetric)withoutPolicy.Metrics[GroundingEvaluator.MetricName]).Value);
        Assert.Equal(1.0, ((NumericMetric)withPolicy.Metrics[GroundingEvaluator.MetricName]).Value);
    }

    [Fact]
    public async Task Pipeline_MarksHallucinatedSummaryForReviewAndSurfacesClaims()
    {
        var report = await new EvaluationPipeline().EvaluateSummaryAsync(
            documentName: "Transcript.txt",
            personaName: "Executive Bullets",
            sourceDocumentText: Transcript,
            summaryText: "### 1. Executive Summary & Strategic Value\n- Olivia Park approved $900,000 on March 3, 2027.\n### 2. Financial & Cost Assessment\n- Strategic budget of $900,000.");

        Assert.Equal("NEEDS_REVIEW", report.OverallStatus);
        Assert.True(report.GroundingScore < GroundingEvaluator.PassThreshold);
        Assert.Contains(report.DiagnosticNotes, n => n.Contains("Olivia Park"));
        Assert.Contains(report.Metrics, m => m.Name == GroundingEvaluator.MetricName && m.Details!.StartsWith("Verified 0 of 3"));
    }
}
