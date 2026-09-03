using Microsoft.Extensions.AI;

namespace FoundrySummarizer.Core.Agentic;

public record AutoFilingWorkflowResult(
    string DocumentName,
    string Summary,
    string Department,
    string SharePointUrl,
    string EmailStatus,
    IReadOnlyList<string> TasksCreated,
    IReadOnlyList<ToolExecutionLogEntry> ToolLogs
);

public class AutoFilingAgent
{
    private readonly IChatClient _chatClient;
    private readonly AutoFilingTools _tools;

    public AutoFilingTools Tools => _tools;

    public AutoFilingAgent(IChatClient chatClient, AutoFilingTools? tools = null)
    {
        _chatClient = chatClient;
        _tools = tools ?? new AutoFilingTools();
    }

    public async Task<AutoFilingWorkflowResult> RunAutoFilingWorkflowAsync(
        string documentName,
        string documentText,
        string summaryText,
        CancellationToken cancellationToken = default)
    {
        _tools.ClearLogs();

        // 1. Tool Call: Determine department
        var department = _tools.DetermineDepartment(summaryText, documentName);

        // 2. Tool Call: Save to SharePoint library
        var sharePointResult = _tools.SaveToSharePoint("Executive_Summaries/2026", documentName, summaryText, department);

        // 3. Tool Call: Dispatch Email notification
        var emailRecipient = department switch
        {
            "Finance & Accounting" => "finance-leadership@enterprise.local",
            "Legal & Compliance" => "legal-counsel@enterprise.local",
            "Engineering & Operations" => "engineering-leads@enterprise.local",
            _ => "executive-board@enterprise.local"
        };
        var emailResult = _tools.EmailSummary(
            recipient: emailRecipient,
            department: department,
            subject: $"[Auto-Filing Agent] New Summary Archived: {documentName}",
            body: summaryText
        );

        // 4. Tool Call: If action items or budget flags exist, auto-create task tickets
        var tasks = new List<string>();
        if (summaryText.Contains("Action Items", StringComparison.OrdinalIgnoreCase) ||
            summaryText.Contains("David", StringComparison.OrdinalIgnoreCase))
        {
            tasks.Add(_tools.CreateTask("Submit VP Expenditure Approval for Hardware", "David (Engineering)", "Friday", "P1"));
        }
        if (summaryText.Contains("Elena", StringComparison.OrdinalIgnoreCase) ||
            summaryText.Contains("Liability", StringComparison.OrdinalIgnoreCase))
        {
            tasks.Add(_tools.CreateTask("Review Vendor SLA & Liability Cap Exception", "Elena (Legal)", "Next Week", "P2"));
        }

        return new AutoFilingWorkflowResult(
            DocumentName: documentName,
            Summary: summaryText,
            Department: department,
            SharePointUrl: sharePointResult,
            EmailStatus: emailResult,
            TasksCreated: tasks,
            ToolLogs: _tools.ExecutionLogs
        );
    }
}
