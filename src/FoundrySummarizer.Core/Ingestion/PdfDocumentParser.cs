using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace FoundrySummarizer.Core.Ingestion;

/// <summary>
/// Extracts text from PDFs by rebuilding lines and paragraphs from word positions.
/// <para>
/// <c>Page.Text</c> concatenates glyphs in content-stream order. Many PDFs position each word separately
/// without space characters, so that produced "Totalinitialengagementfeeis$85,000." with no line breaks,
/// which the model could not summarize reliably and the chunker could not split into paragraphs.
/// </para>
/// Known limitation: multi-column layouts are read line by line across the columns.
/// </summary>
public class PdfDocumentParser : IDocumentParser
{
    // A gap between lines this many times the typical line spacing starts a new paragraph.
    private const double ParagraphGapFactor = 1.6;

    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public IReadOnlyCollection<string> SupportedExtensions => _extensions;

    public bool CanHandle(string extension) => _extensions.Contains(extension);

    /// <summary>Extracts the text of every page that has any.</summary>
    /// <returns>
    /// Page text prefixed with "--- Page N ---" markers, or an empty string when no page has extractable text
    /// (e.g. a scanned PDF, which needs OCR), so callers can tell the user instead of summarizing nothing.
    /// </returns>
    public Task<string> ExtractTextAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        memoryStream.Position = 0;

        var sb = new StringBuilder();
        using var document = PdfDocument.Open(memoryStream);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = ExtractPageText(page);
            if (text.Length == 0) continue;

            sb.AppendLine($"--- Page {page.Number} ---");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString().Trim());
    }

    /// <summary>Groups a page's words into lines (top to bottom, left to right) and lines into paragraphs.</summary>
    internal static string ExtractPageText(Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0) return string.Empty;

        // PDF y grows upwards, so higher baselines come first. A word joins the current line when its baseline
        // is within half a word height of the line's first word; superscripts and small offsets stay on the line.
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var current = lines.LastOrDefault();
            double tolerance = Math.Max(1.0, (current?[0].BoundingBox.Height ?? 0) * 0.5);
            if (current is not null && Math.Abs(current[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= tolerance)
            {
                current.Add(word);
            }
            else
            {
                lines.Add(new List<Word> { word });
            }
        }

        var baselines = lines.Select(l => l.Average(w => w.BoundingBox.Bottom)).ToList();
        var gaps = baselines.Zip(baselines.Skip(1), (upper, lower) => upper - lower).Where(g => g > 0).OrderBy(g => g).ToList();
        // Lower median: paragraph gaps are the minority, so this stays at the normal line spacing even on
        // short pages where half the gaps are paragraph breaks.
        double typicalGap = gaps.Count == 0 ? 0 : gaps[(gaps.Count - 1) / 2];

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0 && typicalGap > 0 && baselines[i - 1] - baselines[i] > typicalGap * ParagraphGapFactor)
            {
                sb.AppendLine();
            }

            sb.AppendLine(string.Join(" ", lines[i].OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
        }

        return sb.ToString().TrimEnd();
    }
}
