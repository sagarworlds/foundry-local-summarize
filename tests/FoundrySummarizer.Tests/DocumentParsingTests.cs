using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using PptPresentation = DocumentFormat.OpenXml.Presentation.Presentation;
using FoundrySummarizer.Core.Ingestion;

namespace FoundrySummarizer.Tests;

/// <summary>Document content that earlier tests did not exercise: Word tables, speaker notes, awkward text shapes.</summary>
public class DocumentParsingTests
{
    [Fact]
    public async Task Docx_ExtractsTablesRowByRow()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Budget breakdown"))),
                new W.Table(
                    Row("Item", "Cost"),
                    Row("GPU servers", "$150,000"),
                    Row("Licences", "$12,500"))));
            main.Document.Save();
        }
        stream.Position = 0;

        var text = await new DocxDocumentParser().ExtractTextAsync(stream, "budget.docx");

        Assert.Contains("Budget breakdown", text);
        Assert.Contains("Item | Cost", text);
        Assert.Contains("GPU servers | $150,000", text);
        Assert.Contains("Licences | $12,500", text);

        static W.TableRow Row(params string[] cells) =>
            new(cells.Select(c => new W.TableCell(new W.Paragraph(new W.Run(new W.Text(c))))));
    }

    [Fact]
    public async Task Pptx_IncludesSpeakerNotes()
    {
        using var stream = new MemoryStream();
        using (var deck = PresentationDocument.Create(stream, DocumentFormat.OpenXml.PresentationDocumentType.Presentation, true))
        {
            var presentation = deck.AddPresentationPart();
            presentation.Presentation = new PptPresentation();

            var slide = presentation.AddNewPart<SlidePart>();
            slide.Slide = new Slide(new CommonSlideData(TextTree("Phase 2 plan")));
            slide.Slide.Save();

            var notes = slide.AddNewPart<NotesSlidePart>();
            notes.NotesSlide = new NotesSlide(new CommonSlideData(TextTree("Mention the Friday approval deadline.")));
            notes.NotesSlide.Save();

            presentation.Presentation.AppendChild(new SlideIdList(new SlideId { Id = 256, RelationshipId = presentation.GetIdOfPart(slide) }));
            presentation.Presentation.Save();
        }
        stream.Position = 0;

        var text = await new PptxDocumentParser().ExtractTextAsync(stream, "deck.pptx");

        Assert.Contains("Phase 2 plan", text);
        Assert.Contains("[Speaker Notes: Mention the Friday approval deadline.]", text);

        static ShapeTree TextTree(string text) => new(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1, Name = "" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(),
            new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties { Id = 2, Name = "Text" },
                    new NonVisualShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new ShapeProperties(),
                new TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text(text))))));
    }

    [Fact]
    public void Chunker_SplitsOneLongLineAtSentences_WithinBudget()
    {
        // PDFs often yield whole pages as one line with no paragraph breaks.
        var line = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"Clause {i} sets a liability cap of ${i * 1000:N0} for the provider."));
        var chunker = new SemanticChunker(maxTokensPerChunk: 100, overlapTokens: 0);

        var chunks = chunker.ChunkText(line, "doc");

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(SemanticChunker.EstimateTokens(c.Text) <= 100, $"chunk of {SemanticChunker.EstimateTokens(c.Text)} tokens"));
        Assert.Contains("Clause 1 sets", chunks[0].Text);
        Assert.Contains("Clause 120 sets", chunks[^1].Text);
    }

    [Fact]
    public void Chunker_SplitsTextWithoutSentencesOrSpaces_WithinBudget()
    {
        // e.g. an embedded URL, hash or base64 blob: no sentence or word boundary to split at.
        var words = string.Join(" ", Enumerable.Repeat("token", 400));
        var blob = new string('x', 3000);
        var chunker = new SemanticChunker(maxTokensPerChunk: 100, overlapTokens: 0);

        var chunks = chunker.ChunkText($"{words} {blob}", "doc");

        Assert.All(chunks, c => Assert.True(SemanticChunker.EstimateTokens(c.Text) <= 100, $"chunk of {SemanticChunker.EstimateTokens(c.Text)} tokens"));
        Assert.Equal(3000, chunks.Sum(c => c.Text.Count(ch => ch == 'x')));   // nothing lost
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Chunker_ReturnsNothingForEmptyText(string text)
    {
        Assert.Empty(new SemanticChunker().ChunkText(text, "doc"));
    }

    [Fact]
    public async Task Pipeline_ReportsAMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.docx");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new DocumentIngestionPipeline().IngestFileAsync(missing));

        Assert.Equal(missing, ex.FileName);
    }
}
