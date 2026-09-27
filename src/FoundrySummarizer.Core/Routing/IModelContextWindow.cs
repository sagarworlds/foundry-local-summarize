namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Tells how much text the active model can take in one request. A chat client offers it through
/// <see cref="Microsoft.Extensions.AI.IChatClient.GetService"/>, so callers that only know <c>IChatClient</c> can
/// size their requests to the model without depending on a particular client.
/// </summary>
public interface IModelContextWindow
{
    /// <summary>The model's context window in tokens (prompt and output together).</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The window, or null when it is not known.</returns>
    Task<int?> GetContextTokensAsync(CancellationToken cancellationToken = default);
}
