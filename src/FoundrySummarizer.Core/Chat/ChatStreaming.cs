using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace FoundrySummarizer.Core.Chat;

/// <summary>Streams a response while collecting it, for callers that show the text as it is written.</summary>
public static class ChatStreaming
{
    // A local model writes 10–60 tokens a second; redrawing the text for each one only costs the UI time, while
    // updates every 50 ms still look continuous.
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets a response, reporting the text written so far to <paramref name="partialText"/> as it arrives. Without a
    /// receiver the response is requested in one piece, exactly like <see cref="IChatClient.GetResponseAsync"/>.
    /// </summary>
    /// <param name="client">The model client.</param>
    /// <param name="messages">The conversation to answer.</param>
    /// <param name="options">Sampling and length settings.</param>
    /// <param name="partialText">
    /// Receives the whole text so far (not just the new piece): the first words at once, then at most every 50 ms,
    /// and once more at the end.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The complete response, with the finish reason the model reported.</returns>
    public static async Task<ChatResponse> StreamResponseAsync(
        this IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IProgress<string>? partialText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (partialText is null)
        {
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }

        var text = new StringBuilder();
        ChatFinishReason? finishReason = null;
        string? modelId = null;
        var sinceReport = new Stopwatch();   // not started: the first words are shown as soon as they arrive
        bool unreported = false;

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            finishReason = update.FinishReason ?? finishReason;
            modelId ??= update.ModelId;
            var piece = update.Text;
            if (string.IsNullOrEmpty(piece)) continue;

            text.Append(piece);
            unreported = true;
            if (!sinceReport.IsRunning || sinceReport.Elapsed >= ReportInterval)
            {
                partialText.Report(text.ToString());
                unreported = false;
                sinceReport.Restart();
            }
        }

        if (unreported) partialText.Report(text.ToString());

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()))
        {
            FinishReason = finishReason,
            ModelId = modelId
        };
    }
}

/// <summary>
/// Passes reports straight to a callback on the reporting thread. Unlike <see cref="Progress{T}"/>, it does not post
/// to a synchronization context, so reports forwarded to another <see cref="IProgress{T}"/> are marshalled only once.
/// </summary>
internal sealed class RelayProgress<T>(Action<T> report) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => report(value);
}
