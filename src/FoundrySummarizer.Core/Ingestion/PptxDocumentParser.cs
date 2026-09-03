using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;

namespace FoundrySummarizer.Core.Ingestion;

public class PptxDocumentParser : IDocumentParser
{
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx", ".pptm", ".potx"
    };

    public IReadOnlyCollection<string> SupportedExtensions => _extensions;

    public bool CanHandle(string extension) => _extensions.Contains(extension);

    public Task<string> ExtractTextAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        using var presentationDoc = PresentationDocument.Open(stream, false);
        var presentationPart = presentationDoc.PresentationPart;
        if (presentationPart?.Presentation?.SlideIdList != null)
        {
            int slideNumber = 1;
            foreach (var slideId in presentationPart.Presentation.SlideIdList.Elements<SlideId>())
            {
                var relId = slideId.RelationshipId?.Value;
                if (relId != null && presentationPart.GetPartById(relId) is SlidePart slidePart)
                {
                    sb.AppendLine($"--- Slide {slideNumber++} ---");
                    if (slidePart.Slide != null)
                    {
                        var texts = slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                            .Select(t => t.Text)
                            .Where(t => !string.IsNullOrWhiteSpace(t));
                        
                        foreach (var text in texts)
                        {
                            sb.AppendLine(text);
                        }
                    }

                    // Extract speaker notes if available
                    if (slidePart.NotesSlidePart?.NotesSlide != null)
                    {
                        var noteTexts = slidePart.NotesSlidePart.NotesSlide
                            .Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                            .Select(t => t.Text)
                            .Where(t => !string.IsNullOrWhiteSpace(t));
                        var notes = string.Join(" ", noteTexts);
                        if (!string.IsNullOrWhiteSpace(notes))
                        {
                            sb.AppendLine($"[Speaker Notes: {notes}]");
                        }
                    }

                    sb.AppendLine();
                }
            }
        }

        return Task.FromResult(sb.ToString().Trim());
    }
}
