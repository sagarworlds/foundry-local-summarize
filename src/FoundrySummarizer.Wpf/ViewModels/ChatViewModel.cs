using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Agentic;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Wpf.ViewModels;

public record ChatMessageItem(
    string Role,
    string Text,
    DateTime Timestamp,
    bool IsUser
);

public partial class ChatViewModel : ObservableObject
{
    private readonly InteractiveSummaryChatAgent _chatAgent;

    [ObservableProperty]
    private string _inputQuestion = string.Empty;

    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    private string _contextDocumentName = string.Empty;

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    public ChatViewModel(HybridChatClientRouter router)
    {
        _chatAgent = new InteractiveSummaryChatAgent(router);
    }

    public void InitializeSession(string docName, string docText, string summaryText)
    {
        ContextDocumentName = docName;
        _chatAgent.InitializeSession(docName, docText, summaryText);
        Messages.Clear();

        Messages.Add(new ChatMessageItem(
            Role: "Assistant",
            Text: $"Hello! I am your Foundry Local Intelligence Co-Pilot. I have ingested and summarized '{docName}'. You can ask me follow-up questions about any detail, risk, owner, or financial figure.",
            Timestamp: DateTime.Now,
            IsUser: false
        ));
    }

    [RelayCommand]
    public async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputQuestion)) return;

        var question = InputQuestion.Trim();
        InputQuestion = string.Empty;

        Messages.Add(new ChatMessageItem("User", question, DateTime.Now, true));
        IsThinking = true;

        try
        {
            var answer = await _chatAgent.AskQuestionAsync(question);
            Messages.Add(new ChatMessageItem("Assistant", answer, DateTime.Now, false));
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageItem("System", $"Error: {ex.Message}", DateTime.Now, false));
        }
        finally
        {
            IsThinking = false;
        }
    }

    [RelayCommand]
    public async Task AskPredefinedQuestionAsync(string question)
    {
        InputQuestion = question;
        await SendMessageAsync();
    }

    [RelayCommand]
    public void ClearChat()
    {
        Messages.Clear();
        _chatAgent.Reset();
    }
}
