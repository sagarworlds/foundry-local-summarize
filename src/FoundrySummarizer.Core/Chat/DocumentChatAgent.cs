using System.Text;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Core.Chat;

/// <summary>
/// Answers follow-up questions about one document and its summary.
/// Each question is sent with only the passages relevant to it (retrieved per question), the summary,
/// and a bounded window of earlier turns. Sending the full document plus the ever-growing conversation
/// on every turn overflowed small local models' context windows, which then truncated the input and
/// answered from whatever was left.
/// </summary>
public class DocumentChatAgent
{
    private readonly IChatClient _chatClient;
    private readonly IPassageRetriever _retriever;
    private readonly ChatConfig _config;

    // Earlier turns are stored as bare question/answer pairs; their passages are not replayed,
    // because each new question retrieves its own evidence.
    private readonly List<ChatMessage> _turns = new();
    private ChatMessage? _systemMessage;
    private string? _previousQuestion;
    private string _documentName = string.Empty;

    /// <summary>The system prompt followed by the retained question/answer turns.</summary>
    public IReadOnlyList<ChatMessage> ChatHistory =>
        _systemMessage is null ? _turns : new[] { _systemMessage }.Concat(_turns).ToList();

    /// <summary>Passages sent with the most recent question, for display or debugging.</summary>
    public IReadOnlyList<RetrievedPassage> LastRetrievedPassages { get; private set; } = Array.Empty<RetrievedPassage>();

    /// <summary>True once a document has been loaded with <see cref="InitializeSession"/>.</summary>
    public bool HasDocument => _systemMessage is not null;

    /// <param name="chatClient">Model client (normally <see cref="FoundryLocalChatClient"/>).</param>
    /// <param name="retriever">Passage retriever; defaults to BM25 over ~250-token passages.</param>
    /// <param name="config">Context budgets; defaults suit 4K-context local models.</param>
    public DocumentChatAgent(IChatClient chatClient, IPassageRetriever? retriever = null, ChatConfig? config = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _retriever = retriever ?? new Bm25PassageRetriever();
        _config = config ?? new ChatConfig();
    }

    /// <summary>Starts a new conversation about <paramref name="documentText"/>, discarding earlier turns.</summary>
    /// <param name="documentName">Shown to the model so it can refer to the document by name.</param>
    /// <param name="documentText">Full document text; indexed into numbered passages.</param>
    /// <param name="summaryText">The generated summary, or empty if none exists yet.</param>
    public void InitializeSession(string documentName, string documentText, string summaryText)
    {
        _turns.Clear();
        _previousQuestion = null;
        LastRetrievedPassages = Array.Empty<RetrievedPassage>();
        _retriever.Index(documentText ?? string.Empty);

        _documentName = documentName ?? string.Empty;
        _systemMessage = BuildSystemMessage(_documentName, summaryText);
    }

    /// <summary>
    /// Replaces the summary the model sees while keeping the conversation, e.g. after the user regenerates
    /// the summary of the same document with another persona.
    /// </summary>
    /// <param name="summaryText">The new summary.</param>
    /// <exception cref="InvalidOperationException"><see cref="InitializeSession"/> has not been called.</exception>
    public void UpdateSummary(string summaryText)
    {
        if (_systemMessage is null)
        {
            throw new InvalidOperationException("Load a document before adding its summary.");
        }

        _systemMessage = BuildSystemMessage(_documentName, summaryText);
    }

    /// <summary>Clears the conversation but keeps the document and summary, so a fresh line of questioning can start.</summary>
    public void ClearConversation()
    {
        _turns.Clear();
        _previousQuestion = null;
        LastRetrievedPassages = Array.Empty<RetrievedPassage>();
    }

    private static ChatMessage BuildSystemMessage(string documentName, string? summaryText)
    {
        var summary = string.IsNullOrWhiteSpace(summaryText) ? "(No summary has been generated yet.)" : summaryText.Trim();
        var systemPrompt = $"""
            You are a document assistant running locally on Microsoft Foundry Local.
            You answer follow-up questions about the document '{documentName}'.

            [GENERATED SUMMARY]
            {summary}

            RULES:
            1. Answer only from the <passages> sent with each question and from the summary above. Passages are exact excerpts of the document; the summary may be incomplete or wrong, so prefer the passages when they differ.
            2. Cite the passages you used as [P#], for example: "The budget is $150,000 [P2]."
            3. Quote figures, dates and names exactly as written.
            4. If the passages and the summary do not contain the answer, reply "The document does not say." and do not guess.
            5. Be concise.
            """;

        return new ChatMessage(ChatRole.System, systemPrompt);
    }

    /// <summary>Answers <paramref name="question"/> using retrieved passages and recent turns.</summary>
    /// <param name="question">The user's question; blank input returns an empty answer.</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>The model's answer.</returns>
    /// <exception cref="InvalidOperationException"><see cref="InitializeSession"/> has not been called.</exception>
    /// <exception cref="LocalModelUnavailableException">No local model could answer; the question is not added to the history.</exception>
    public async Task<string> AskQuestionAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return string.Empty;
        if (_systemMessage is null)
        {
            throw new InvalidOperationException("Load a document before asking questions.");
        }

        question = question.Trim();
        LastRetrievedPassages = _retriever.Retrieve(BuildRetrievalQuery(question), _config.MaxPassageTokens);

        var messages = new List<ChatMessage> { _systemMessage };
        messages.AddRange(_turns);
        messages.Add(new ChatMessage(ChatRole.User, BuildQuestionPrompt(question, LastRetrievedPassages)));

        var options = new ChatOptions { Temperature = 0.1f, MaxOutputTokens = _config.MaxAnswerTokens };
        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
        var answer = response.Text ?? string.Empty;

        RememberTurn(question, answer);
        _previousQuestion = question;
        return answer;
    }

    /// <summary>Clears the document, summary and conversation.</summary>
    public void Reset()
    {
        ClearConversation();
        _systemMessage = null;
        _documentName = string.Empty;
        _retriever.Index(string.Empty);
    }

    /// <summary>
    /// Follow-ups such as "and when is it due?" carry almost no searchable terms, so the previous
    /// question is added to the query when the new one has fewer than two content words.
    /// </summary>
    private string BuildRetrievalQuery(string question)
    {
        int contentTerms = Bm25PassageRetriever.Tokenize(question).Count();
        return contentTerms < 2 && _previousQuestion is not null
            ? $"{question} {_previousQuestion}"
            : question;
    }

    private static string BuildQuestionPrompt(string question, IReadOnlyList<RetrievedPassage> passages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<passages>");
        if (passages.Count == 0)
        {
            sb.AppendLine("(No passage of the document matched this question.)");
        }
        foreach (var passage in passages)
        {
            sb.AppendLine($"[P{passage.Number}] {passage.Text}");
            sb.AppendLine();
        }
        sb.AppendLine("</passages>");
        sb.AppendLine();
        sb.Append("Question: ").Append(question);
        return sb.ToString();
    }

    private void RememberTurn(string question, string answer)
    {
        _turns.Add(new ChatMessage(ChatRole.User, question));
        _turns.Add(new ChatMessage(ChatRole.Assistant, answer));

        int maxMessages = Math.Max(0, _config.MaxHistoryTurns) * 2;
        if (_turns.Count > maxMessages)
        {
            _turns.RemoveRange(0, _turns.Count - maxMessages);
        }
    }
}
