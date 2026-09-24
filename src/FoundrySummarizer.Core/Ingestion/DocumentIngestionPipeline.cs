namespace FoundrySummarizer.Core.Ingestion;

/// <summary>A supported file could not be parsed, e.g. it is corrupt, encrypted or not really in that format.</summary>
public sealed class DocumentReadException : Exception
{
    /// <param name="message">Which file failed and why.</param>
    /// <param name="innerException">The parser's own exception.</param>
    public DocumentReadException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Extracts plain text from supported document formats.</summary>
public interface IDocumentIngestionPipeline
{
    /// <summary>File extensions (with leading dot) that can be read.</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>Extracts text from <paramref name="stream"/>, choosing the parser by <paramref name="fileName"/>'s extension.</summary>
    /// <exception cref="NotSupportedException">The file format is not supported.</exception>
    /// <exception cref="DocumentReadException">The file could not be parsed.</exception>
    Task<IngestionResult> IngestAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Extracts text from the file at <paramref name="filePath"/>.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="NotSupportedException">The file format is not supported.</exception>
    /// <exception cref="DocumentReadException">The file could not be parsed.</exception>
    Task<IngestionResult> IngestFileAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>Picks the parser for a file by extension (.txt/.md, .docx, .pptx, .pdf) and returns its text.</summary>
public class DocumentIngestionPipeline : IDocumentIngestionPipeline
{
    private readonly IReadOnlyList<IDocumentParser> _parsers;

    /// <param name="parsers">Parsers to use; defaults to plain text, Word, PowerPoint and PDF.</param>
    public DocumentIngestionPipeline(IEnumerable<IDocumentParser>? parsers = null)
    {
        var parserList = parsers?.ToList();
        _parsers = parserList is { Count: > 0 }
            ? parserList
            : new IDocumentParser[] { new PlainTextParser(), new DocxDocumentParser(), new PptxDocumentParser(), new PdfDocumentParser() };
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions =>
        _parsers.SelectMany(p => p.SupportedExtensions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <inheritdoc />
    public async Task<IngestionResult> IngestAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(fileName);
        var parser = _parsers.FirstOrDefault(p => p.CanHandle(ext))
            ?? throw new NotSupportedException($"File format '{ext}' is not supported. Supported extensions: {string.Join(", ", SupportedExtensions)}");

        string text;
        try
        {
            text = await parser.ExtractTextAsync(stream, fileName, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            // Each parser library (OpenXml, PdfPig) throws its own exception types for corrupt or encrypted files;
            // one type lets callers report "this file could not be read" without knowing every library.
            throw new DocumentReadException($"'{fileName}' could not be read as a {ext} file: {ex.Message}", ex);
        }

        return new IngestionResult { FileName = fileName, ExtractedText = text };
    }

    /// <inheritdoc />
    public async Task<IngestionResult> IngestFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Document file not found: {filePath}", filePath);
        }

        await using var stream = File.OpenRead(filePath);
        return await IngestAsync(stream, Path.GetFileName(filePath), cancellationToken);
    }
}
