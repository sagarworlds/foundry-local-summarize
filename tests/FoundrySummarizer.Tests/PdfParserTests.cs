using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using FoundrySummarizer.Core.Ingestion;

namespace FoundrySummarizer.Tests;

public class PdfParserTests
{
    /// <summary>
    /// Builds a PDF whose words are positioned individually with no space glyphs (common in real PDFs),
    /// with a larger gap after the title, followed by a blank page like a scanned image-only page.
    /// </summary>
    private static byte[] BuildWordPositionedPdf(bool includeText = true)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        if (includeText)
        {
            string[][] lines =
            {
                new[] { "MASTER", "SERVICES", "AGREEMENT" },
                new[] { "Total", "initial", "engagement", "fee", "is", "$85,000." },
                new[] { "Termination", "requires", "thirty", "(30)", "days", "notice." }
            };

            double y = 780;
            for (int i = 0; i < lines.Length; i++)
            {
                double x = 50;
                foreach (var word in lines[i])
                {
                    page.AddText(word, 12, new PdfPoint(x, y), font);
                    x += word.Length * 9 + 8;
                }
                y -= i == 0 ? 40 : 16;
            }
        }

        builder.AddPage(PageSize.A4);
        return builder.Build();
    }

    private static Task<string> ExtractAsync(byte[] pdf) =>
        new PdfDocumentParser().ExtractTextAsync(new MemoryStream(pdf), "test.pdf");

    [Fact]
    public async Task Extract_RestoresSpacesLinesAndParagraphs()
    {
        var text = await ExtractAsync(BuildWordPositionedPdf());

        Assert.Equal(
            "--- Page 1 ---\n" +
            "MASTER SERVICES AGREEMENT\n" +
            "\n" +
            "Total initial engagement fee is $85,000.\n" +
            "Termination requires thirty (30) days notice.",
            text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Extract_ReturnsEmptyTextWhenNoPageHasText()
    {
        Assert.Equal(string.Empty, await ExtractAsync(BuildWordPositionedPdf(includeText: false)));
    }

    [Fact]
    public async Task Pipeline_ChunksExtractedPdfByParagraph()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdf-test-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, BuildWordPositionedPdf());
        try
        {
            var result = await new DocumentIngestionPipeline().IngestFileAsync(path);

            Assert.Contains("Total initial engagement fee is $85,000.", result.ExtractedText);
            Assert.NotEmpty(result.Chunks);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
