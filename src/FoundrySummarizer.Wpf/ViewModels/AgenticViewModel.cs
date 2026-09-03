using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Agentic;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Wpf.ViewModels;

public partial class AgenticViewModel : ObservableObject
{
    private readonly AutoFilingAgent _agent;

    [ObservableProperty]
    private string _documentName = "Sample_Proposal.docx";

    [ObservableProperty]
    private string _documentText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _department = "Pending Categorization";

    [ObservableProperty]
    private string _sharePointPath = "Pending Archival";

    [ObservableProperty]
    private string _emailStatus = "Pending Dispatch";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "Ready to run Auto-Filing Agent.";

    public ObservableCollection<ToolExecutionLogEntry> ExecutionLogs { get; } = new();
    public ObservableCollection<string> TasksCreated { get; } = new();

    public AgenticViewModel(HybridChatClientRouter router)
    {
        _agent = new AutoFilingAgent(router);
    }

    public void UpdateContext(string docName, string docText, string summaryText)
    {
        DocumentName = docName;
        DocumentText = docText;
        SummaryText = summaryText;
    }

    [RelayCommand]
    public async Task RunWorkflowAsync()
    {
        if (string.IsNullOrWhiteSpace(SummaryText))
        {
            StatusMessage = "Please generate a summary first before running the Auto-Filing agent!";
            return;
        }

        IsRunning = true;
        StatusMessage = "Agent executing tool invocation pipeline...";

        try
        {
            var result = await _agent.RunAutoFilingWorkflowAsync(DocumentName, DocumentText, SummaryText);

            Department = result.Department;
            SharePointPath = result.SharePointUrl;
            EmailStatus = result.EmailStatus;

            ExecutionLogs.Clear();
            foreach (var log in result.ToolLogs)
            {
                ExecutionLogs.Add(log);
            }

            TasksCreated.Clear();
            foreach (var task in result.TasksCreated)
            {
                TasksCreated.Add(task);
            }

            StatusMessage = $"Auto-Filing Agent completed: Archived to SharePoint and notified {Department} stakeholders.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Agent Workflow Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    public void ClearLogs()
    {
        ExecutionLogs.Clear();
        TasksCreated.Clear();
        Department = "Pending Categorization";
        SharePointPath = "Pending Archival";
        EmailStatus = "Pending Dispatch";
        StatusMessage = "Logs cleared.";
    }
}
