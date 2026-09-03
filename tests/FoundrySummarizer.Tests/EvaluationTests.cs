using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Evaluation;
using FoundrySummarizer.Core.Evaluation.Evaluators;

namespace FoundrySummarizer.Tests;

public class EvaluationTests
{
    [Fact]
    public async Task CompletenessEvaluator_ScoresCoverage()
    {
        var evaluator = new CompletenessEvaluator();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Project Helios deployment requires GPU acceleration hardware and $150,000 budget with approval by VP Marcus.")
        };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Executive Summary: Project Helios requires GPU hardware and $150,000 budget with VP Marcus approval."));

        var result = await evaluator.EvaluateAsync(messages, response);
        var metric = result.Metrics[CompletenessEvaluator.MetricName];

        Assert.NotNull(metric);
        Assert.False(metric.Interpretation?.Failed);
    }

    [Fact]
    public async Task GroundingEvaluator_VerifiesFactsAgainstSource()
    {
        var evaluator = new GroundingEvaluator();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Total expenditure request is $150,000.")
        };

        // Grounded summary
        var responseGrounded = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Budget requested: $150,000."));
        var resultGrounded = await evaluator.EvaluateAsync(messages, responseGrounded);
        var metricGrounded = resultGrounded.Metrics[GroundingEvaluator.MetricName];
        Assert.False(metricGrounded.Interpretation?.Failed);

        // Hallucinated summary with ungrounded amount
        var responseHallucinated = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Budget requested: $950,000."));
        var resultHallucinated = await evaluator.EvaluateAsync(messages, responseHallucinated);
        var metricHallucinated = resultHallucinated.Metrics[GroundingEvaluator.MetricName];
        Assert.True(metricHallucinated.Interpretation?.Failed);
    }

    [Fact]
    public async Task ContentSafetyGuardrail_CatchesPIIAndKeys()
    {
        var evaluator = new ContentSafetyGuardrailEvaluator();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Please summarize customer data.")
        };

        // Unsafe summary with leaked SSN and API key
        var unsafeResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Customer SSN: 123-45-6789. Master Key: sk-1234567890abcdef1234567890."));
        var result = await evaluator.EvaluateAsync(messages, unsafeResponse);
        var metric = result.Metrics[ContentSafetyGuardrailEvaluator.MetricName];

        Assert.NotNull(metric);
        Assert.True(metric.Interpretation?.Failed);
        Assert.NotEmpty(metric.Diagnostics);
    }

    [Fact]
    public async Task EvaluationPipeline_ProducesComprehensiveReport()
    {
        var pipeline = new EvaluationPipeline();
        var report = await pipeline.EvaluateSummaryAsync(
            documentName: "Helios_Proposal.docx",
            personaName: "Executive Bullets",
            sourceDocumentText: "Project Helios requires $150,000 for local edge hardware.",
            summaryText: "### 1. Executive Summary\nProject Helios requires $150,000 for local hardware.\n### 2. Financial Assessment\nAmount: $150,000."
        );

        Assert.NotNull(report);
        Assert.Equal("Helios_Proposal.docx", report.DocumentName);
        Assert.True(report.ContentSafetyPassed);
        Assert.True(report.CompletenessScore > 0);
        Assert.True(report.PersonaAdherenceScore > 0);
        Assert.True(report.GroundingScore > 0);
        Assert.Contains(report.OverallStatus, new[] { "EXCELLENT", "GOOD" });
    }
}
