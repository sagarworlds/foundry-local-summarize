using System.Text.RegularExpressions;
using FoundrySummarizer.Core.Ingestion;

namespace FoundrySummarizer.Core.Chat;

/// <summary>A document passage selected as evidence for a question.</summary>
/// <param name="Number">1-based passage number, stable for the indexed document and used for [P#] citations.</param>
/// <param name="Text">Passage text exactly as it appears in the document.</param>
/// <param name="Score">Relevance score; 0 when the passage was included without matching (whole-document mode).</param>
public record RetrievedPassage(int Number, string Text, double Score);

/// <summary>Finds the parts of one document that are relevant to a question.</summary>
public interface IPassageRetriever
{
    /// <summary>Number of passages in the indexed document.</summary>
    int PassageCount { get; }

    /// <summary>Replaces the current index with <paramref name="documentText"/>.</summary>
    void Index(string documentText);

    /// <summary>
    /// Returns the most relevant passages that fit within <paramref name="maxTokens"/>, in document order.
    /// </summary>
    /// <param name="query">The question (optionally with extra context terms).</param>
    /// <param name="maxTokens">Estimated token budget for all returned passages together.</param>
    IReadOnlyList<RetrievedPassage> Retrieve(string query, int maxTokens);
}

/// <summary>
/// Okapi BM25 lexical retrieval over document passages. Questions about a document usually name the
/// exact figure, person, clause or term they are about, which lexical matching finds reliably and
/// without an embedding model.
/// </summary>
public class Bm25PassageRetriever : IPassageRetriever
{
    // Standard BM25 parameters: K1 controls term-frequency saturation, B the document-length normalisation.
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    // "150,000" and "$150,000" must both index as "150000", otherwise a question quoting the figure
    // with a different thousands format never matches.
    private static readonly Regex ThousandsSeparator = new(@"(?<=\d),(?=\d{3}\b)", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "did", "do", "does", "for", "from", "has", "have",
        "how", "i", "in", "is", "it", "its", "me", "of", "on", "or", "tell", "that", "the", "their", "there", "this",
        "to", "was", "we", "were", "what", "when", "where", "which", "who", "whom", "why", "will", "with", "you", "about",
        "document", "please", "any", "our", "they", "them", "these", "those", "would", "should", "could"
    };

    private readonly SemanticChunker _chunker;
    private IReadOnlyList<DocumentChunk> _passages = Array.Empty<DocumentChunk>();
    private List<Dictionary<string, int>> _termFrequencies = new();
    private Dictionary<string, int> _documentFrequencies = new(StringComparer.Ordinal);
    private double _averageLength;

    /// <param name="chunker">Splits the document into passages; defaults to ~250-token passages without overlap.</param>
    public Bm25PassageRetriever(SemanticChunker? chunker = null)
    {
        // No overlap: overlapping passages would be retrieved together and waste the context budget.
        _chunker = chunker ?? new SemanticChunker(maxTokensPerChunk: 250, overlapTokens: 0);
    }

    /// <inheritdoc />
    public int PassageCount => _passages.Count;

    /// <inheritdoc />
    public void Index(string documentText)
    {
        _passages = _chunker.ChunkText(documentText ?? string.Empty, "chat");
        _termFrequencies = _passages
            .Select(p => Tokenize(p.Text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal))
            .ToList();

        _documentFrequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in _termFrequencies.SelectMany(tf => tf.Keys))
        {
            _documentFrequencies[term] = _documentFrequencies.GetValueOrDefault(term) + 1;
        }

        _averageLength = _termFrequencies.Count == 0 ? 0 : _termFrequencies.Average(tf => tf.Values.Sum());
    }

    /// <inheritdoc />
    public IReadOnlyList<RetrievedPassage> Retrieve(string query, int maxTokens)
    {
        if (_passages.Count == 0 || maxTokens <= 0)
        {
            return Array.Empty<RetrievedPassage>();
        }

        // A document that fits the budget is sent whole: nothing can be missed and ranking adds only risk.
        if (_passages.Sum(p => p.TokenCount) <= maxTokens)
        {
            return _passages.Select(p => new RetrievedPassage(p.Index + 1, p.Text, 0)).ToList();
        }

        var queryTerms = Tokenize(query ?? string.Empty).Distinct().ToList();
        var ranked = _passages
            .Select((p, i) => (Passage: p, Score: Score(queryTerms, i)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Passage.Index)
            .ToList();

        var selected = new List<RetrievedPassage>();
        int used = 0;
        foreach (var (passage, score) in ranked)
        {
            if (used + passage.TokenCount > maxTokens) continue;
            selected.Add(new RetrievedPassage(passage.Index + 1, passage.Text, score));
            used += passage.TokenCount;
        }

        // Document order reads more naturally and keeps related passages adjacent for the model.
        return selected.OrderBy(p => p.Number).ToList();
    }

    private double Score(IReadOnlyList<string> queryTerms, int passageIndex)
    {
        var tf = _termFrequencies[passageIndex];
        double length = tf.Values.Sum();
        double score = 0;
        int n = _passages.Count;

        foreach (var term in queryTerms)
        {
            if (!tf.TryGetValue(term, out int frequency)) continue;

            int df = _documentFrequencies[term];
            // BM25 IDF with +1 inside the log so very common terms never score negative.
            double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
            double norm = frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * length / Math.Max(1, _averageLength)));
            score += idf * norm;
        }

        return score;
    }

    /// <summary>
    /// Lower-cases, normalises thousands separators, drops stop words and applies light plural stemming
    /// so that "owners" matches "owner" and "liabilities" matches "liability".
    /// </summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        var normalized = ThousandsSeparator.Replace(text.ToLowerInvariant(), string.Empty);
        foreach (Match match in TokenRegex.Matches(normalized))
        {
            var token = match.Value;
            if (StopWords.Contains(token)) continue;
            yield return Stem(token);
        }
    }

    private static string Stem(string token)
    {
        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal)) return token[..^3] + "y";
        if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal)) return token[..^1];
        return token;
    }
}
