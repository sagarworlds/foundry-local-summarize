using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Chat;
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

    /// <summary>The sizes one summary works with, for the model in use.</summary>
    /// <param name="SinglePassTokens">Longest text summarized (or condensed) in one call.</param>
    /// <param name="Parts">Splits a longer text into parts.</param>
    /// <param name="Pieces">Re-reads a part whose notes hit the output limit, in pieces half the size.</param>
    private sealed record Budget(int SinglePassTokens, SemanticChunker Parts, SemanticChunker Pieces);

    /// <param name="chatClient">
    /// Model client (normally <see cref="Routing.FoundryLocalChatClient"/>). When it offers an
    /// <see cref="IModelContextWindow"/>, documents are read in one pass whenever they fit the model's window.
    /// </param>
    /// <param name="promptyEngine">Renders the persona prompt for the final summary.</param>
    /// <param name="config">Token budgets; the defaults suit 4K-context local models when the window is unknown.</param>
    public MultiPartSummarizer(IChatClient chatClient, IPromptyEngine promptyEngine, SummarizationConfig? config = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _promptyEngine = promptyEngine ?? throw new ArgumentNullException(nameof(promptyEngine));
        _config = config ?? new SummarizationConfig();
    }

    /// <inheritdoc />
    public async Task<int> GetSinglePassTokenLimitAsync(PromptyDocument persona, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var window = _chatClient.GetService<IModelContextWindow>();
        int? contextTokens = window is null ? null : await window.GetContextTokensAsync(cancellationToken);

        var emptyPrompt = _promptyEngine.RenderChatMessages(persona, new Dictionary<string, string> { ["documentText"] = string.Empty });
        var promptTokens = emptyPrompt.Sum(message => SemanticChunker.EstimateTokens(message.Text ?? string.Empty));
        return SummaryBudget.SinglePassTokens(_config, contextTokens, persona.ModelConfig.MaxTokens, promptTokens);
    }

    private async Task<Budget> GetBudgetAsync(PromptyDocument persona, CancellationToken cancellationToken)
    {
        var singlePass = await GetSinglePassTokenLimitAsync(persona, cancellationToken);
        var partTokens = SummaryBudget.PartTokens(_config, singlePass);
        return new Budget(
            singlePass,
            new SemanticChunker(partTokens, _config.MapChunkOverlapTokens),
            new SemanticChunker(Math.Max(50, partTokens / 2), overlapTokens: 0));
    }

    /// <inheritdoc />
    public async Task<SummarizationResult> SummarizeAsync(
        SummarizationRequest request,
        IProgress<SummarizationProgress>? progress = null,
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
        int cutOffNoteParts = 0;
        bool usedMultiPart = false;

        var budget = await GetBudgetAsync(request.Persona, cancellationToken);
        if (SemanticChunker.EstimateTokens(sourceText) > budget.SinglePassTokens)
        {
            var notes = await CondenseAsync(sourceText, request.Persona, budget, progress, cancellationToken);
            sourceText = $"(Faithful notes taken from all {notes.PartCount} parts of a long document, in order.)\n\n{notes.Text}";
            partCount = notes.PartCount;
            cutOffNoteParts = notes.CutOffParts;
            usedMultiPart = true;
        }

        var status = usedMultiPart
            ? $"Writing the final summary from notes on {partCount} parts..."
            : "Writing the summary...";
        progress?.Report(new SummarizationProgress(status));

        // The final summary is streamed so the user reads it as it is written instead of waiting for the whole of it.
        var draft = progress is null ? null : new RelayProgress<string>(text => progress.Report(new SummarizationProgress(status, text)));
        var variables = new Dictionary<string, string> { ["documentText"] = sourceText };
        var messages = _promptyEngine.RenderChatMessages(request.Persona, variables);
        var response = await _chatClient.StreamResponseAsync(messages, request.Persona.ToChatOptions(), draft, cancellationToken);

        return new SummarizationResult(response.Text ?? string.Empty, usedMultiPart, partCount, response.WasCutOff(), cutOffNoteParts);
    }

    /// <summary>
    /// Replaces the document with per-part notes, repeating on the notes themselves until they fit the
    /// single-pass budget or <see cref="SummarizationConfig.MaxCondenseRounds"/> is reached.
    /// </summary>
    /// <returns>The notes, the number of parts in the first pass, and how many parts' notes stayed cut off.</returns>
    private async Task<(string Text, int PartCount, int CutOffParts)> CondenseAsync(
        string text,
        PromptyDocument persona,
        Budget budget,
        IProgress<SummarizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        int firstPassParts = 0;
        int cutOffParts = 0;
        int rounds = Math.Max(1, _config.MaxCondenseRounds);

        for (int round = 1; round <= rounds; round++)
        {
            var parts = budget.Parts.ChunkText(text, $"pass-{round}");
            if (round == 1) firstPassParts = parts.Count;

            var notes = new List<string>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SummarizationProgress(round == 1
                    ? $"Reading part {i + 1} of {parts.Count}..."
                    : $"Condensing notes (pass {round}), part {i + 1} of {parts.Count}..."));

                var partNotes = await NotesForPartAsync(parts[i].Text, i + 1, parts.Count, persona, budget, progress, cancellationToken);
                if (partNotes.CutOff) cutOffParts++;
                if (partNotes.Text.Length > 0)
                {
                    notes.Add($"[Part {i + 1} of {parts.Count}]\n{partNotes.Text}");
                }
            }

            text = string.Join("\n\n", notes);
            if (SemanticChunker.EstimateTokens(text) <= budget.SinglePassTokens)
            {
                break;
            }
        }

        return (text, firstPassParts, cutOffParts);
    }

    /// <summary>
    /// Takes notes on one part. Notes that stop at the output limit have lost the facts after that point, so the part
    /// is read again in smaller pieces, each with the full output limit for its notes. Pieces are not split again,
    /// which keeps the number of model calls bounded.
    /// </summary>
    /// <returns>The notes, and whether any of them still stopped at the output limit.</returns>
    private async Task<(string Text, bool CutOff)> NotesForPartAsync(
        string partText,
        int partNumber,
        int partTotal,
        PromptyDocument persona,
        Budget budget,
        IProgress<SummarizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var notes = await TakeNotesAsync(partText, partNumber, partTotal, persona, cancellationToken);
        if (!notes.CutOff) return notes;

        var pieces = budget.Pieces.ChunkText(partText, $"part-{partNumber}");
        if (pieces.Count < 2) return notes;

        var pieceNotes = new List<string>(pieces.Count);
        bool stillCutOff = false;
        for (int p = 0; p < pieces.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SummarizationProgress(
                $"Part {partNumber} of {partTotal} has more detail than one set of notes holds; reading it in {pieces.Count} pieces ({p + 1} of {pieces.Count})..."));
            var piece = await TakeNotesAsync(pieces[p].Text, partNumber, partTotal, persona, cancellationToken);
            stillCutOff |= piece.CutOff;
            if (piece.Text.Length > 0) pieceNotes.Add(piece.Text);
        }

        return (string.Join("\n", pieceNotes), stillCutOff);
    }

    /// <returns>Notes for the part (empty if it had nothing relevant), and whether they stopped at the output limit.</returns>
    private async Task<(string Text, bool CutOff)> TakeNotesAsync(
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
        return (string.Equals(text, NoneMarker, StringComparison.OrdinalIgnoreCase) ? string.Empty : text, response.WasCutOff());
    }
}
