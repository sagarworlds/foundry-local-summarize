using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Ingestion;

namespace FoundrySummarizer.Core.Chat;

/// <summary>Suggests follow-up questions about one document.</summary>
public interface IFollowUpQuestionGenerator
{
    /// <summary>Suggests questions a reader of this document is likely to ask next.</summary>
    /// <param name="documentName">Shown to the model for context.</param>
    /// <param name="summary">The document's summary: the most compact picture of what it covers.</param>
    /// <param name="documentText">The full text; its opening is used when there is no summary.</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>Up to the configured number of questions; empty when the model suggested none.</returns>
    /// <exception cref="Routing.LocalModelUnavailableException">No local model could answer.</exception>
    Task<IReadOnlyList<string>> SuggestAsync(string documentName, string summary, string documentText, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the local model for short questions specific to the document, so the one-click prompts in the chat
/// are about what was uploaded (its figures, parties, deadlines, clauses) rather than generic.
/// </summary>
public sealed class FollowUpQuestionGenerator : IFollowUpQuestionGenerator
{
    // Small models answer a list request with numbering, bullets or quotes; these are stripped from each line.
    private static readonly Regex ListMarker = new(@"^\s*(?:[-*•]|\d+[.)]|Q\d*[:.)])\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int MaxQuestionLength = 140;

    // A summary is already condensed; without one, the document's opening says what it is about. Either keeps the
    // prompt small enough for a 4K-context model.
    private const int MaxContextTokens = 1200;

    private readonly IChatClient _chatClient;
    private readonly int _count;

    /// <param name="chatClient">Model client (normally <see cref="Routing.FoundryLocalChatClient"/>).</param>
    /// <param name="count">How many questions to suggest.</param>
    public FollowUpQuestionGenerator(IChatClient chatClient, int count = 4)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _count = Math.Max(1, count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SuggestAsync(string documentName, string summary, string documentText, CancellationToken cancellationToken = default)
    {
        var context = string.IsNullOrWhiteSpace(summary)
            ? Truncate(documentText ?? string.Empty, MaxContextTokens)
            : Truncate(summary, MaxContextTokens);
        if (string.IsNullOrWhiteSpace(context)) return Array.Empty<string>();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, $"""
                You suggest follow-up questions that a reader of a document would ask next.
                RULES:
                1. Every question must be about this specific document: name its actual people, parties, amounts, dates, deadlines, clauses or decisions.
                2. Every question must be answerable from the document. Do not ask about things it does not mention.
                3. Keep each question under 15 words.
                4. Write exactly {_count} questions, one per line, each ending with "?". No numbering, no introduction.
                """),
            new(ChatRole.User, $"""
                Document: {documentName}

                <content>
                {context}
                </content>

                Write {_count} follow-up questions about this document.
                """)
        };

        // A little temperature so questions vary between documents instead of repeating one template.
        var options = new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 60 * _count };
        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
        return ParseQuestions(response.Text ?? string.Empty, _count);
    }

    /// <summary>Extracts clean, distinct questions from the model's answer.</summary>
    /// <param name="text">The model output.</param>
    /// <param name="max">Maximum number of questions to return.</param>
    public static IReadOnlyList<string> ParseQuestions(string text, int max) =>
        text.Split('\n')
            .Select(line => ListMarker.Replace(line, string.Empty).Trim().Trim('"', '\'', '“', '”').Trim())
            .Where(line => line.EndsWith('?') && line.Length is > 8 and <= MaxQuestionLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();

    private static string Truncate(string text, int maxTokens)
    {
        int maxChars = maxTokens * 4; // same ≈4 characters per token estimate as SemanticChunker
        return SemanticChunker.EstimateTokens(text) <= maxTokens ? text.Trim() : text[..Math.Min(text.Length, maxChars)].Trim() + " …";
    }
}
