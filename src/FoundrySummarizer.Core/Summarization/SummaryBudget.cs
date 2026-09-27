using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Core.Summarization;

/// <summary>
/// How long a document can be before it is read in parts. With the model's context window known, it is whatever
/// the window has left after the summary's own length and the prompt, less a margin; otherwise it is the configured
/// <see cref="SummarizationConfig.MaxSinglePassTokens"/>. Reading a document in one pass instead of in parts needs
/// one model call instead of one per part plus one, and the model sees the whole document at once.
/// </summary>
public static class SummaryBudget
{
    // Token counts are estimated at about 4 characters per token. Figures, names and non-English text take more
    // tokens than that, so a fifth of the room is kept free rather than risk the runtime cutting the document short.
    private const double EstimateMargin = 0.8;

    /// <summary>The smallest budget used, even for a window too small for the summary style (the notes still fit).</summary>
    public const int MinimumSinglePassTokens = 300;

    /// <summary>The longest document, in estimated tokens, to summarize in one call.</summary>
    /// <param name="config">Configured budgets.</param>
    /// <param name="contextTokens">The model's context window, or null when unknown.</param>
    /// <param name="summaryOutputTokens">Room the summary itself needs (the style's <c>max_tokens</c>).</param>
    /// <param name="promptTokens">The summary prompt without the document.</param>
    public static int SinglePassTokens(SummarizationConfig config, int? contextTokens, int summaryOutputTokens, int promptTokens)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (contextTokens is not { } context) return config.MaxSinglePassTokens;

        var room = (int)((context - summaryOutputTokens - promptTokens) * EstimateMargin);
        return Math.Clamp(room, MinimumSinglePassTokens, Math.Max(MinimumSinglePassTokens, config.SinglePassTokenCap));
    }

    /// <summary>
    /// Size of each part of a longer document: the configured part size, but at most half the single-pass budget,
    /// so the notes on two parts always fit where one part did and condensing makes progress.
    /// </summary>
    public static int PartTokens(SummarizationConfig config, int singlePassTokens)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Math.Min(config.MapChunkTokens, Math.Max(50, singlePassTokens / 2));
    }
}
