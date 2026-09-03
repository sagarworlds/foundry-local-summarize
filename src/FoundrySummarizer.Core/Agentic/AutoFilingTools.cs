using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace FoundrySummarizer.Core.Agentic;

public record ToolExecutionLogEntry(
    DateTime Timestamp,
    string ToolName,
    string ParametersSummary,
    string ResultSummary,
    bool IsSuccess
);

public class AutoFilingTools
{
    private readonly List<ToolExecutionLogEntry> _executionLogs = new();
    private readonly object _lock = new();

    public IReadOnlyList<ToolExecutionLogEntry> ExecutionLogs
    {
        get
        {
            lock (_lock) return _executionLogs.ToList();
        }
    }

    public void ClearLogs()
    {
        lock (_lock) _executionLogs.Clear();
    }

    [Description("Analyzes the summary content and determines the optimal organizational department (Finance, Legal, Engineering, HR, Executive)")]
    public string DetermineDepartment(
        [Description("The text or summary of the document")] string summaryContent,
        [Description("Optional file name or document type")] string? documentType = null)
    {
        string dept;
        var text = (summaryContent + " " + documentType).ToLowerInvariant();

        if (text.Contains("liability") || text.Contains("indemn") || text.Contains("contract") || text.Contains("legal") || text.Contains("clause"))
        {
            dept = "Legal & Compliance";
        }
        else if (text.Contains("cost") || text.Contains("budget") || text.Contains("dollar") || text.Contains("$") || text.Contains("financial") || text.Contains("forecast"))
        {
            dept = "Finance & Accounting";
        }
        else if (text.Contains("architecture") || text.Contains("hardware") || text.Contains("engineering") || text.Contains("phase") || text.Contains("api") || text.Contains("code"))
        {
            dept = "Engineering & Operations";
        }
        else
        {
            dept = "Executive Strategy";
        }

        Log("DetermineDepartment", $"summaryLength={summaryContent.Length}, type={documentType}", $"Categorized as '{dept}'", true);
        return dept;
    }

    [Description("Saves the finalized executive summary to the enterprise SharePoint document library")]
    public string SaveToSharePoint(
        [Description("Target SharePoint document library path")] string libraryPath,
        [Description("File name for the saved summary")] string fileName,
        [Description("Markdown summary content")] string summaryContent,
        [Description("Department category")] string department)
    {
        var sanitized = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}_Summary.md";
        var fullPath = $"{libraryPath.TrimEnd('/')}/{department.Replace(" ", "_")}/{sanitized}";
        var result = $"Successfully archived summary to SharePoint: https://sharepoint.enterprise.local/sites/{fullPath} (Size: {summaryContent.Length} bytes, Version: 1.0)";

        Log("SaveToSharePoint", $"library={libraryPath}, file={sanitized}, dept={department}", result, true);
        return result;
    }

    [Description("Dispatches email notification with the generated summary to departmental stakeholders")]
    public string EmailSummary(
        [Description("Recipient email address or alias")] string recipient,
        [Description("Department name")] string department,
        [Description("Email subject line")] string subject,
        [Description("Body of the email / summary text")] string body)
    {
        var result = $"Email notification successfully dispatched via Enterprise SMTP to '{recipient}' for department [{department}] with subject: '{subject}' (Payload: {body.Length} characters).";
        Log("EmailSummary", $"to={recipient}, dept={department}, subject={subject}", result, true);
        return result;
    }

    [Description("Creates an action item task in Azure DevOps / Jira project tracking board")]
    public string CreateTask(
        [Description("Title of the task")] string title,
        [Description("Owner or assignee")] string owner,
        [Description("Target deadline or due date")] string deadline,
        [Description("Priority level (P1, P2, P3)")] string priority)
    {
        var ticketId = "TASK-" + Random.Shared.Next(1000, 9999);
        var result = $"Work item {ticketId} successfully created: '{title}' assigned to {owner} (Due: {deadline}, Priority: {priority})";
        Log("CreateTask", $"title={title}, owner={owner}, deadline={deadline}, priority={priority}", result, true);
        return result;
    }

    private void Log(string toolName, string parameters, string result, bool success)
    {
        lock (_lock)
        {
            _executionLogs.Add(new ToolExecutionLogEntry(DateTime.Now, toolName, parameters, result, success));
        }
    }

    public IList<AIFunction> AsAIFunctions()
    {
        return new List<AIFunction>
        {
            AIFunctionFactory.Create(DetermineDepartment, nameof(DetermineDepartment)),
            AIFunctionFactory.Create(SaveToSharePoint, nameof(SaveToSharePoint)),
            AIFunctionFactory.Create(EmailSummary, nameof(EmailSummary)),
            AIFunctionFactory.Create(CreateTask, nameof(CreateTask))
        };
    }
}
