using System.Text;

namespace FoundrySummarizer.Core.Ingestion;

public class SemanticChunker
{
    private readonly int _maxTokensPerChunk;
    private readonly int _overlapTokens;

    public SemanticChunker(int maxTokensPerChunk = 600, int overlapTokens = 50)
    {
        _maxTokensPerChunk = Math.Max(50, maxTokensPerChunk);
        _overlapTokens = Math.Clamp(overlapTokens, 0, _maxTokensPerChunk / 2);
    }

    public IReadOnlyList<DocumentChunk> ChunkText(string text, string documentId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<DocumentChunk>();
        }

        var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<DocumentChunk>();
        var currentChunkBuilder = new StringBuilder();
        int chunkIndex = 0;

        foreach (var rawPara in paragraphs)
        {
            var paragraph = rawPara.Trim();
            if (string.IsNullOrEmpty(paragraph)) continue;

            int currentTokens = EstimateTokens(currentChunkBuilder.ToString());
            int paraTokens = EstimateTokens(paragraph);

            if (currentTokens + paraTokens > _maxTokensPerChunk && currentChunkBuilder.Length > 0)
            {
                var chunkContent = currentChunkBuilder.ToString().Trim();
                chunks.Add(new DocumentChunk(
                    Id: $"{documentId}-chunk-{chunkIndex}",
                    DocumentId: documentId,
                    Index: chunkIndex++,
                    Text: chunkContent,
                    TokenCount: EstimateTokens(chunkContent)
                ));

                currentChunkBuilder.Clear();

                // Add overlap from previous chunk
                if (_overlapTokens > 0)
                {
                    var sentences = chunkContent.Split(new[] { ". ", "? ", "! " }, StringSplitOptions.RemoveEmptyEntries);
                    if (sentences.Length > 1)
                    {
                        currentChunkBuilder.Append(sentences[^1].Trim()).Append(". ");
                    }
                }
            }

            // If a single paragraph itself is larger than max tokens, split it by sentences
            if (paraTokens > _maxTokensPerChunk)
            {
                var sentences = paragraph.Split(new[] { ". ", "? ", "! " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var sentence in sentences)
                {
                    var sentenceTokens = EstimateTokens(sentence);
                    if (EstimateTokens(currentChunkBuilder.ToString()) + sentenceTokens > _maxTokensPerChunk && currentChunkBuilder.Length > 0)
                    {
                        var chunkText = currentChunkBuilder.ToString().Trim();
                        chunks.Add(new DocumentChunk(
                            Id: $"{documentId}-chunk-{chunkIndex}",
                            DocumentId: documentId,
                            Index: chunkIndex++,
                            Text: chunkText,
                            TokenCount: EstimateTokens(chunkText)
                        ));
                        currentChunkBuilder.Clear();
                    }
                    currentChunkBuilder.Append(sentence).Append(". ");
                }
            }
            else
            {
                currentChunkBuilder.AppendLine(paragraph).AppendLine();
            }
        }

        if (currentChunkBuilder.Length > 0)
        {
            var finalChunk = currentChunkBuilder.ToString().Trim();
            if (!string.IsNullOrEmpty(finalChunk))
            {
                chunks.Add(new DocumentChunk(
                    Id: $"{documentId}-chunk-{chunkIndex}",
                    DocumentId: documentId,
                    Index: chunkIndex,
                    Text: finalChunk,
                    TokenCount: EstimateTokens(finalChunk)
                ));
            }
        }

        return chunks;
    }

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
    }
}
