using Microsoft.Extensions.AI;

namespace FoundrySummarizer.Core.Agentic;

public class InteractiveSummaryChatAgent
{
    private readonly IChatClient _chatClient;
    private readonly List<ChatMessage> _chatHistory = new();
    private string _currentDocument = string.Empty;
    private string _currentSummary = string.Empty;

    public IReadOnlyList<ChatMessage> ChatHistory => _chatHistory;

    public InteractiveSummaryChatAgent(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public void InitializeSession(string documentName, string documentText, string summaryText)
    {
        _currentDocument = documentText;
        _currentSummary = summaryText;
        _chatHistory.Clear();

        var systemPrompt = $"""
        You are an Interactive Document Intelligence Co-Pilot running locally on Microsoft Foundry Local.
        You have analyzed the document '{documentName}' and produced an initial summary.

        CONTEXT:
        [GENERATED SUMMARY]
        {_currentSummary}

        [ORIGINAL SOURCE DOCUMENT]
        {_currentDocument}

        INSTRUCTIONS:
        Answer user questions specifically about this summary and document.
        When asked why a risk was flagged, reference the exact clauses or numbers.
        If a user asks for clarification on an action item, identify the relevant excerpt from the source document.
        Maintain professional, concise, and grounded answers.
        """;

        _chatHistory.Add(new ChatMessage(ChatRole.System, systemPrompt));
    }

    public async Task<string> AskQuestionAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return string.Empty;

        _chatHistory.Add(new ChatMessage(ChatRole.User, question));

        var response = await _chatClient.GetResponseAsync(_chatHistory, new ChatOptions { Temperature = 0.2f }, cancellationToken);
        var answer = response.Text ?? string.Empty;

        _chatHistory.Add(new ChatMessage(ChatRole.Assistant, answer));
        return answer;
    }

    public void Reset()
    {
        _chatHistory.Clear();
        _currentDocument = string.Empty;
        _currentSummary = string.Empty;
    }
}
