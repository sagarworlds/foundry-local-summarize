using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using PptPresentation = DocumentFormat.OpenXml.Presentation.Presentation;
using FoundrySummarizer.Core.Ingestion;

namespace FoundrySummarizer.Tests;

public class IngestionTests
{
    [Fact]
    public async Task PlainTextParser_ExtractsFullContent()
    {
        var parser = new PlainTextParser();
        var sample = "Executive summary text.\nSecond line with details.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sample));

        var extracted = await parser.ExtractTextAsync(stream, "test.txt");
        Assert.Contains("Executive summary text", extracted);
        Assert.Contains("Second line with details", extracted);
    }

    [Fact]
    public void SemanticChunker_CreatesAccurateChunks()
    {
        var chunker = new SemanticChunker(maxTokensPerChunk: 50, overlapTokens: 10);
        var longText = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Paragraph {i}: This is an enterprise document section outlining phase milestones and technical objectives for project intelligence."));

        var chunks = chunker.ChunkText(longText, "doc-1");
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 0));
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(1, chunks[1].Index);
    }

    [Fact]
    public async Task DocxDocumentParser_ExtractsParagraphsAndGeneratesSample()
    {
        // Generate real sample docx in samples folder
        Directory.CreateDirectory("samples");
        var docxPath = Path.Combine("samples", "sample_project_proposal.docx");

        using (var wordDoc = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("PROJECT HELIOS: ENTERPRISE SUMMARIZATION ARCHITECTURE"))),
                new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("This document specifies the Phase 2 deployment of Microsoft Foundry Local with C# and Microsoft.Extensions.AI."))),
                new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Total Capital Expenditure: $150,000 for local edge hardware."))),
                new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Policy Notice: Per Q2 Financial Framework, amounts over $100,000 require VP Marcus Vance approval.")))
            ));
            mainPart.Document.Save();
        }

        var parser = new DocxDocumentParser();
        await using var stream = File.OpenRead(docxPath);
        var extracted = await parser.ExtractTextAsync(stream, "sample_project_proposal.docx");

        Assert.Contains("PROJECT HELIOS", extracted);
        Assert.Contains("$150,000", extracted);
    }

    [Fact]
    public async Task PptxDocumentParser_ExtractsSlidesAndGeneratesSample()
    {
        Directory.CreateDirectory("samples");
        var pptxPath = Path.Combine("samples", "sample_executive_deck.pptx");

        using (var presDoc = PresentationDocument.Create(pptxPath, PresentationDocumentType.Presentation))
        {
            var presPart = presDoc.AddPresentationPart();
            presPart.Presentation = new PptPresentation();

            var slidePart = presPart.AddNewPart<SlidePart>();
            slidePart.Slide = new Slide(new CommonSlideData(new ShapeTree(
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1, Name = "" },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(),
                new Shape(
                    new NonVisualShapeProperties(
                        new NonVisualDrawingProperties { Id = 2, Name = "Title" },
                        new NonVisualShapeDrawingProperties(),
                        new ApplicationNonVisualDrawingProperties()),
                    new ShapeProperties(),
                    new TextBody(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text("Project Helios - Executive Summary Deck"))),
                        new A.Paragraph(new A.Run(new A.Text("Local-First Privacy Architecture ($0 Inference Cost)"))),
                        new A.Paragraph(new A.Run(new A.Text("Phase 2 Budget: $150,000 requiring VP sign-off.")))
                    )
                )
            )));
            slidePart.Slide.Save();

            var slideIdList = presPart.Presentation.AppendChild(new SlideIdList());
            slideIdList.AppendChild(new SlideId { Id = 256, RelationshipId = presPart.GetIdOfPart(slidePart) });
            presPart.Presentation.Save();
        }

        var parser = new PptxDocumentParser();
        await using var stream = File.OpenRead(pptxPath);
        var extracted = await parser.ExtractTextAsync(stream, "sample_executive_deck.pptx");

        Assert.Contains("Slide 1", extracted);
        Assert.Contains("Project Helios", extracted);
    }

    [Fact]
    public async Task Pipeline_ExtractsTextFromSupportedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minutes-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "Executive Meeting Minutes.\n\nPhase 2 hardware request is $150,000.");
        try
        {
            var result = await new DocumentIngestionPipeline().IngestFileAsync(path);

            Assert.Equal(Path.GetFileName(path), result.FileName);
            Assert.Contains("$150,000", result.ExtractedText);
            Assert.True(result.EstimatedTokens > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Pipeline_RejectsUnsupportedFormats()
    {
        using var stream = new MemoryStream(new byte[16]);
        await Assert.ThrowsAsync<NotSupportedException>(() => new DocumentIngestionPipeline().IngestAsync(stream, "meeting.wav"));
    }

    [Fact]
    public async Task Pipeline_ReportsCorruptFilesAsDocumentReadException()
    {
        using var stream = new MemoryStream("this is not a zip package"u8.ToArray());
        var ex = await Assert.ThrowsAsync<DocumentReadException>(() => new DocumentIngestionPipeline().IngestAsync(stream, "broken.docx"));
        Assert.Contains("broken.docx", ex.Message);
    }
}
