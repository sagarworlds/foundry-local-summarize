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
/// <param name="WasCutOff">True when the summary stopped at the model's output limit, so its end is missing.</param>
/// <param name="CutOffNoteParts">
/// Parts whose notes still reached the output limit after being re-read in smaller pieces; details from them may be
/// missing from the summary.
/// </param>
public record SummarizationResult(string Summary, bool UsedMultiPart, int PartCount, bool WasCutOff = false, int CutOffNoteParts = 0);

/// <summary>A progress report while a summary is being produced.</summary>
/// <param name="Status">What is happening now, e.g. "Reading part 2 of 5...".</param>
/// <param name="Draft">The summary written so far; empty until the model starts writing the final summary.</param>
public record SummarizationProgress(string Status, string Draft = "");

/// <summary>Produces a persona summary for a document of any length.</summary>
public interface IDocumentSummarizer
{
    /// <summary>Summarizes <see cref="SummarizationRequest.DocumentText"/> with the request's persona.</summary>
    /// <param name="request">What to summarize and how.</param>
    /// <param name="progress">Optional receiver for status messages and, while the final summary is written, its text so far.</param>
    /// <param name="cancellationToken">Cancels outstanding model calls.</param>
    /// <returns>The summary and how it was produced.</returns>
    /// <exception cref="ArgumentException">The document text is empty.</exception>
    /// <exception cref="Routing.LocalModelUnavailableException">No local model could answer.</exception>
    Task<SummarizationResult> SummarizeAsync(
        SummarizationRequest request,
        IProgress<SummarizationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The longest document, in estimated tokens, summarized in one pass with the current model and
    /// <paramref name="persona"/>; longer documents are read in parts.
    /// </summary>
    /// <param name="persona">The summary style (its length limit takes room from the model's context window).</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<int> GetSinglePassTokenLimitAsync(PromptyDocument persona, CancellationToken cancellationToken = default);
}
