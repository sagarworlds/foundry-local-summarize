using FoundrySummarizer.Core.Agentic;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Tests;

public class AgenticToolsTests
{
    [Fact]
    public void AutoFilingTools_CategorizesDepartmentAccurately()
    {
        var tools = new AutoFilingTools();

        var legalDept = tools.DetermineDepartment("Contract agreement with indemnification and liability clause.");
        Assert.Equal("Legal & Compliance", legalDept);

        var financeDept = tools.DetermineDepartment("Budget request and quarterly financial expenditure forecast for $150,000.");
        Assert.Equal("Finance & Accounting", financeDept);

        var engDept = tools.DetermineDepartment("Architecture refactoring for GPU acceleration server deployment.");
        Assert.Equal("Engineering & Operations", engDept);
    }

    [Fact]
    public void AutoFilingTools_ExecutesSharePointAndEmailTools()
    {
        var tools = new AutoFilingTools();

        var sp = tools.SaveToSharePoint("Enterprise_Docs", "Proposal.docx", "Executive summary text", "Finance & Accounting");
        Assert.Contains("sharepoint.enterprise.local", sp);
        Assert.Contains("Proposal.docx_Summary.md", sp);

        var email = tools.EmailSummary("cfo@enterprise.local", "Finance & Accounting", "New Summary", "Budget breakdown");
        Assert.Contains("Enterprise SMTP", email);
        Assert.Contains("cfo@enterprise.local", email);

        Assert.True(tools.ExecutionLogs.Count >= 2);
    }

    [Fact]
    public async Task AutoFilingAgent_RunsWorkflowEndToEnd()
    {
        var client = new LocalFoundryFallbackClient();
        var agent = new AutoFilingAgent(client);

        var result = await agent.RunAutoFilingWorkflowAsync(
            documentName: "Helios_Proposal.docx",
            documentText: "Project Helios budget of $150,000 assigned to David Chen.",
            summaryText: "Executive Summary: $150,000 budget allocation. David Chen to submit VP approval."
        );

        Assert.NotNull(result);
        Assert.Equal("Helios_Proposal.docx", result.DocumentName);
        Assert.Contains("Finance", result.Department);
        Assert.Contains("sharepoint", result.SharePointUrl);
        Assert.NotEmpty(result.TasksCreated);
        Assert.True(result.ToolLogs.Count >= 3);
    }

    [Fact]
    public async Task InteractiveSummaryChatAgent_AnswersContextualQuestions()
    {
        var client = new LocalFoundryFallbackClient();
        var chat = new InteractiveSummaryChatAgent(client);

        chat.InitializeSession(
            documentName: "Meeting_Transcript.txt",
            documentText: "Elena (Legal) to review vendor contract liability caps.",
            summaryText: "Action Items: Elena assigned to contract review."
        );

        var answer1 = await chat.AskQuestionAsync("Who is assigned to the contract review?");
        Assert.Contains("Elena", answer1);

        var answer2 = await chat.AskQuestionAsync("Why was this risk flagged?");
        Assert.Contains("policy", answer2);
    }
}
