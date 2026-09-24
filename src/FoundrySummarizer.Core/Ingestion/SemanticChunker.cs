using System.Text;
using System.Text.RegularExpressions;

namespace FoundrySummarizer.Core.Ingestion;

/// <summary>
/// Splits text into token-bounded chunks, preferring paragraph, then line, then sentence boundaries,
/// and carrying a small sentence overlap between consecutive chunks so context is not cut mid-thought.
/// </summary>
public class SemanticChunker
{
    // Split after sentence-ending punctuation while keeping it attached to the sentence.
    private static readonly Regex SentenceBoundary = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    private readonly int _maxTokensPerChunk;
    private readonly int _overlapTokens;

    /// <param name="maxTokensPerChunk">Upper bound (estimated tokens) for each chunk; minimum 50.</param>
    /// <param name="overlapTokens">Approximate tokens of trailing sentences repeated at the start of the next chunk.</param>
    public SemanticChunker(int maxTokensPerChunk = 600, int overlapTokens = 50)
    {
        _maxTokensPerChunk = Math.Max(50, maxTokensPerChunk);
        _overlapTokens = Math.Clamp(overlapTokens, 0, _maxTokensPerChunk / 2);
    }

    /// <summary>Chunks <paramref name="text"/> into pieces no larger than the configured token budget.</summary>
    /// <param name="text">Source text; empty or whitespace yields no chunks.</param>
    /// <param name="documentId">Identifier used to build each chunk id.</param>
    /// <returns>Chunks in document order with sequential indexes.</returns>
    public IReadOnlyList<DocumentChunk> ChunkText(string text, string documentId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<DocumentChunk>();
        }

        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<DocumentChunk>();
        var current = new StringBuilder();
        // False while `current` holds only the overlap carried from the previous chunk, which must
        // never be emitted as a chunk of its own.
        bool hasNewContent = false;

        void Flush()
        {
            var content = current.ToString().Trim();
            current.Clear();
            hasNewContent = false;
            if (content.Length == 0) return;

            int index = chunks.Count;
            chunks.Add(new DocumentChunk(
                Id: $"{documentId}-chunk-{index}",
                DocumentId: documentId,
                Index: index,
                Text: content,
                TokenCount: EstimateTokens(content)
            ));

            var overlap = BuildOverlap(content);
            if (overlap.Length > 0)
            {
                current.Append(overlap).Append('\n');
            }
        }

        foreach (var rawPara in paragraphs)
        {
            var paragraph = rawPara.Trim();
            if (paragraph.Length == 0) continue;

            // A paragraph that fits is kept whole; an oversized one is broken into smaller pieces
            // that are packed exactly like paragraphs, so no single chunk exceeds the budget.
            var pieces = EstimateTokens(paragraph) > _maxTokensPerChunk
                ? SplitOversized(paragraph)
                : new[] { paragraph };

            foreach (var piece in pieces)
            {
                if (hasNewContent && EstimateTokens(current.ToString()) + EstimateTokens(piece) > _maxTokensPerChunk)
                {
                    Flush();
                }

                // Overlap is best-effort: drop it when overlap + piece would exceed the budget.
                if (!hasNewContent && EstimateTokens(current.ToString()) + EstimateTokens(piece) > _maxTokensPerChunk)
                {
                    current.Clear();
                }

                current.Append(piece).Append(pieces.Count > 1 ? "\n" : "\n\n");
                hasNewContent = true;
            }
        }

        var remaining = current.ToString().Trim();
        if (hasNewContent && remaining.Length > 0)
        {
            chunks.Add(new DocumentChunk(
                Id: $"{documentId}-chunk-{chunks.Count}",
                DocumentId: documentId,
                Index: chunks.Count,
                Text: remaining,
                TokenCount: EstimateTokens(remaining)
            ));
        }

        return chunks;
    }

    /// <summary>Rough token estimate (≈4 characters per token for English text).</summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
    }

    /// <summary>
    /// Breaks an oversized paragraph by lines (transcripts, bullet lists), then sentences, then fixed
    /// word windows as a last resort for text with no punctuation at all.
    /// </summary>
    private IReadOnlyList<string> SplitOversized(string paragraph)
    {
        var pieces = new List<string>();
        foreach (var rawLine in paragraph.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (EstimateTokens(line) <= _maxTokensPerChunk)
            {
                pieces.Add(line);
                continue;
            }

            foreach (var sentence in SentenceBoundary.Split(line))
            {
                if (sentence.Length == 0) continue;
                if (EstimateTokens(sentence) <= _maxTokensPerChunk)
                {
                    pieces.Add(sentence);
                }
                else
                {
                    pieces.AddRange(SplitByWords(sentence));
                }
            }
        }

        return pieces;
    }

    private IEnumerable<string> SplitByWords(string text)
    {
        int maxChars = _maxTokensPerChunk * 4;
        var window = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (window.Length > 0 && window.Length + 1 + word.Length > maxChars)
            {
                yield return window.ToString();
                window.Clear();
            }

            // A single "word" longer than the budget (e.g. a base64 blob) is cut into fixed slices.
            if (word.Length > maxChars)
            {
                for (int i = 0; i < word.Length; i += maxChars)
                {
                    yield return word.Substring(i, Math.Min(maxChars, word.Length - i));
                }
                continue;
            }

            if (window.Length > 0) window.Append(' ');
            window.Append(word);
        }

        if (window.Length > 0) yield return window.ToString();
    }

    /// <summary>Returns the trailing sentences of a chunk that fit within the overlap budget.</summary>
    private string BuildOverlap(string chunkContent)
    {
        if (_overlapTokens == 0) return string.Empty;

        var sentences = SentenceBoundary.Split(chunkContent);
        if (sentences.Length < 2) return string.Empty;

        var selected = new List<string>();
        int tokens = 0;
        for (int i = sentences.Length - 1; i > 0; i--)
        {
            int sentenceTokens = EstimateTokens(sentences[i]);
            if (tokens + sentenceTokens > _overlapTokens) break;
            selected.Insert(0, sentences[i].Trim());
            tokens += sentenceTokens;
        }

        return string.Join(" ", selected);
    }
}
