using FoundrySummarizer.Core.Personas;

namespace FoundrySummarizer.Core.Summarization;

/// <summary>Input for a persona summary.</summary>
/// <param name="Persona">Persona whose prompts and sampling settings shape the final summary.</param>
/// <param name="DocumentText">Full extracted document text.</param>
public record SummarizationRequest(PromptyDocument Persona, string DocumentText);

/// <summary>Outcome of a summary run.</summary>
/// <param name="Summary">The persona-formatted summary text.</param>
/// <param name="UsedMultiPart">True when the document was split into parts and summarized from notes.</param>
/// <param name="PartCount">Number of parts the document was read in (1 for a single pass).</param>
public record SummarizationResult(string Summary, bool UsedMultiPart, int PartCount);

/// <summary>Produces a persona summary for a document of any length.</summary>
public interface IDocumentSummarizer
{
    /// <summary>Summarizes <see cref="SummarizationRequest.DocumentText"/> with the request's persona.</summary>
    /// <param name="request">What to summarize and how.</param>
    /// <param name="progress">Optional receiver for human-readable status messages.</param>
    /// <param name="cancellationToken">Cancels outstanding model calls.</param>
    /// <returns>The summary and how it was produced.</returns>
    /// <exception cref="ArgumentException">The document text is empty.</exception>
    /// <exception cref="Routing.LocalModelUnavailableException">No local model could answer.</exception>
    Task<SummarizationResult> SummarizeAsync(
        SummarizationRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
