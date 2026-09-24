using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Core.Summarization;

/// <summary>
/// Summarizes documents that are too long for a small local model's context window.
/// Short documents go to the persona prompt in one call. Long ones are split into parts, the model takes
/// faithful notes on each part (map), and the persona summary is written from the combined notes (reduce).
/// Without this, an over-long prompt is silently truncated by the runtime and the model summarizes only
/// what it saw, or fills the gaps with invented content.
/// </summary>
public class MultiPartSummarizer : IDocumentSummarizer
{
    /// <summary>System prompt for the per-part note-taking (map) step.</summary>
    public const string NoteTakerSystemPrompt = """
        You take notes on one part of a longer document so that a summary can later be written from your notes alone.

        RULES:
        1. Copy only information that appears in the part below. Never add, infer or guess.
        2. Keep every figure, amount, percentage, date, deadline, name, role, party, section number and defined term exactly as written.
        3. Write short bullets with no introduction or commentary.
        4. If the part contains nothing relevant, reply with exactly: NONE
        """;

    private const string NoneMarker = "NONE";

    private readonly IChatClient _chatClient;
    private readonly IPromptyEngine _promptyEngine;
    private readonly SummarizationConfig _config;
    private readonly SemanticChunker _chunker;

    /// <param name="chatClient">Model client (normally the hybrid router).</param>
    /// <param name="promptyEngine">Renders the persona prompt for the final summary.</param>
    /// <param name="config">Token budgets; defaults are sized for 4K-context local models.</param>
    public MultiPartSummarizer(IChatClient chatClient, IPromptyEngine promptyEngine, SummarizationConfig? config = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _promptyEngine = promptyEngine ?? throw new ArgumentNullException(nameof(promptyEngine));
        _config = config ?? new SummarizationConfig();

        // A part must be smaller than the single-pass budget, otherwise splitting could never make progress.
        int partTokens = Math.Min(_config.MapChunkTokens, Math.Max(50, _config.MaxSinglePassTokens / 2));
        _chunker = new SemanticChunker(partTokens, _config.MapChunkOverlapTokens);
    }

    /// <inheritdoc />
    public async Task<SummarizationResult> SummarizeAsync(
        SummarizationRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Persona);
        if (string.IsNullOrWhiteSpace(request.DocumentText))
        {
            throw new ArgumentException("There is no document text to summarize.", nameof(request));
        }

        string sourceText = request.DocumentText;
        int partCount = 1;
        bool usedMultiPart = false;

        if (request.AllowMultiPart && SemanticChunker.EstimateTokens(sourceText) > _config.MaxSinglePassTokens)
        {
            var condensed = await CondenseAsync(sourceText, request.Persona, progress, cancellationToken);
            if (condensed is { } notes)
            {
                sourceText = $"(Faithful notes taken from all {notes.PartCount} parts of a long document, in order.)\n\n{notes.Text}";
                partCount = notes.PartCount;
                usedMultiPart = true;
            }
        }

        progress?.Report(usedMultiPart
            ? $"Writing the final summary from notes on {partCount} parts..."
            : "Generating summary...");

        var variables = new Dictionary<string, string>
        {
            ["documentText"] = sourceText,
            ["groundingContext"] = request.GroundingContext ?? string.Empty
        };
        var messages = _promptyEngine.RenderChatMessages(request.Persona, variables);
        var response = await _chatClient.GetResponseAsync(messages, request.Persona.ToChatOptions(), cancellationToken);

        return new SummarizationResult(response.Text ?? string.Empty, usedMultiPart, partCount);
    }

    /// <summary>
    /// Replaces the document with per-part notes, repeating on the notes themselves until they fit the
    /// single-pass budget or <see cref="SummarizationConfig.MaxCondenseRounds"/> is reached.
    /// </summary>
    /// <returns>The notes and the number of parts in the first pass, or null when no real model answered.</returns>
    private async Task<(string Text, int PartCount)?> CondenseAsync(
        string text,
        PromptyDocument persona,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        int firstPassParts = 0;
        int rounds = Math.Max(1, _config.MaxCondenseRounds);

        for (int round = 1; round <= rounds; round++)
        {
            var parts = _chunker.ChunkText(text, $"pass-{round}");
            if (round == 1) firstPassParts = parts.Count;

            var notes = new List<string>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(round == 1
                    ? $"Reading part {i + 1} of {parts.Count}..."
                    : $"Condensing notes (pass {round}), part {i + 1} of {parts.Count}...");

                var partNotes = await TakeNotesAsync(parts[i].Text, i + 1, parts.Count, persona, cancellationToken);
                if (partNotes is null)
                {
                    // No real model answered (demo fallback). Notes from canned text would be meaningless,
                    // so let the single-pass call produce the clearly labelled fallback output instead.
                    return null;
                }

                if (partNotes.Length > 0)
                {
                    notes.Add($"[Part {i + 1} of {parts.Count}]\n{partNotes}");
                }
            }

            text = string.Join("\n\n", notes);
            if (SemanticChunker.EstimateTokens(text) <= _config.MaxSinglePassTokens)
            {
                break;
            }
        }

        return (text, firstPassParts);
    }

    /// <returns>Notes for the part, an empty string if it had nothing relevant, or null if the answer came from the demo fallback.</returns>
    private async Task<string?> TakeNotesAsync(
        string partText,
        int partNumber,
        int partTotal,
        PromptyDocument persona,
        CancellationToken cancellationToken)
    {
        var userPrompt = $"""
            The final summary will be: {persona.Name} ({persona.Description})

            <document_part number="{partNumber}" of="{partTotal}">
            {partText}
            </document_part>

            Write notes for this part, using only the headings that apply:
            Figures & amounts:
            Dates & deadlines:
            People, parties & owners:
            Tasks, decisions & commitments:
            Clauses, obligations & risks:
            Other key points:
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, NoteTakerSystemPrompt),
            new(ChatRole.User, userPrompt)
        };

        // Temperature 0: note-taking is extraction, and any creativity here becomes a "fact" downstream.
        var options = new ChatOptions { Temperature = 0f, MaxOutputTokens = _config.MapMaxOutputTokens };
        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
        var text = (response.Text ?? string.Empty).Trim();

        if (text.StartsWith(HybridChatClientRouter.FallbackNotice.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return string.Equals(text, NoneMarker, StringComparison.OrdinalIgnoreCase) ? string.Empty : text;
    }
}
