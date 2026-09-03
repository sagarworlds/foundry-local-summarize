namespace FoundrySummarizer.Core.Ingestion;

public interface IDocumentIngestionPipeline
{
    IReadOnlyCollection<string> SupportedExtensions { get; }
    Task<IngestionResult> IngestAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
    Task<IngestionResult> IngestFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<IngestionResult> IngestTextAsync(string text, string documentName = "InputText.txt", CancellationToken cancellationToken = default);
}

public class DocumentIngestionPipeline : IDocumentIngestionPipeline
{
    private readonly List<IDocumentParser> _parsers;
    private readonly SemanticChunker _chunker;

    public DocumentIngestionPipeline(IEnumerable<IDocumentParser>? parsers = null, SemanticChunker? chunker = null)
    {
        var parserList = parsers?.ToList();
        _parsers = parserList != null && parserList.Count > 0 
            ? parserList 
            : new List<IDocumentParser>
            {
                new PlainTextParser(),
                new DocxDocumentParser(),
                new PptxDocumentParser(),
                new PdfDocumentParser(),
                new AudioTranscriptionService()
            };

        _chunker = chunker ?? new SemanticChunker();
    }

    public IReadOnlyCollection<string> SupportedExtensions =>
        _parsers.SelectMany(p => p.SupportedExtensions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IngestionResult> IngestAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(fileName);
        var parser = _parsers.FirstOrDefault(p => p.CanHandle(ext))
            ?? throw new NotSupportedException($"File format '{ext}' is not supported. Supported extensions: {string.Join(", ", SupportedExtensions)}");

        var text = await parser.ExtractTextAsync(stream, fileName, cancellationToken);
        var docId = Guid.NewGuid().ToString("N");
        var chunks = _chunker.ChunkText(text, docId);

        return new IngestionResult
        {
            DocumentId = docId,
            FileName = fileName,
            FileExtension = ext,
            ExtractedText = text,
            Chunks = chunks,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Parser"] = parser.GetType().Name,
                ["ChunkCount"] = chunks.Count.ToString(),
                ["IngestedAt"] = DateTime.UtcNow.ToString("o")
            }
        };
    }

    public async Task<IngestionResult> IngestFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Document file not found: {filePath}", filePath);
        }

        await using var stream = File.OpenRead(filePath);
        return await IngestAsync(stream, Path.GetFileName(filePath), cancellationToken);
    }

    public Task<IngestionResult> IngestTextAsync(string text, string documentName = "InputText.txt", CancellationToken cancellationToken = default)
    {
        var docId = Guid.NewGuid().ToString("N");
        var chunks = _chunker.ChunkText(text, docId);

        var result = new IngestionResult
        {
            DocumentId = docId,
            FileName = documentName,
            FileExtension = Path.GetExtension(documentName),
            ExtractedText = text,
            Chunks = chunks,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Parser"] = nameof(PlainTextParser),
                ["ChunkCount"] = chunks.Count.ToString(),
                ["IngestedAt"] = DateTime.UtcNow.ToString("o")
            }
        };

        return Task.FromResult(result);
    }
}
