using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Thrown when no local model can answer: Foundry Local is not running or not installed, the model cannot be
/// loaded, or the request timed out or failed. The message says what went wrong and how to fix it.
/// </summary>
public sealed class LocalModelUnavailableException : Exception
{
    /// <param name="message">What went wrong and how to fix it, in plain language.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public LocalModelUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Sends every request to the model served by Foundry Local (or another OpenAI-compatible local server).
/// Before each request it makes sure the service is reachable and the model is loaded. It never substitutes
/// canned text: when no model can answer it throws <see cref="LocalModelUnavailableException"/>, so the user
/// sees the reason instead of a summary that did not come from their document.
/// </summary>
public sealed class FoundryLocalChatClient : IChatClient
{
    private readonly FoundryOptions _options;
    private readonly FoundryLocalService _localService;

    /// <param name="options">Local endpoint, model and timeout settings.</param>
    /// <param name="localService">Local service manager; created from <paramref name="options"/> when omitted.</param>
    public FoundryLocalChatClient(FoundryOptions options, FoundryLocalService? localService = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _localService = localService ?? new FoundryLocalService(options);
    }

    /// <summary>
    /// The model requests are sent to. Starts as the configured <c>Local.ModelId</c> and, when
    /// <c>Local.AutoSelectModel</c> is on, becomes the best preferred model the service offers.
    /// </summary>
    public string ActiveModelId => _localService.ActiveModelId;

    /// <summary>The discovered Foundry Local endpoint once found, else the configured one.</summary>
    public Uri ActiveEndpoint => _localService.Endpoint ?? new Uri(_options.GetEffectiveLocalEndpoint());

    /// <summary>Endpoint, model and (when unreachable) the reason, for status displays. Does not load a model.</summary>
    public Task<LocalModelStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _localService.CheckAsync(ensureModelLoaded: false, cancellationToken);

    /// <inheritdoc />
    /// <exception cref="LocalModelUnavailableException">No local model could answer; the message says why.</exception>
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = await GetReadyClientAsync(cancellationToken);
        try
        {
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (IsRequestFailure(ex, cancellationToken))
        {
            throw new LocalModelUnavailableException(DescribeRequestFailure(ex), ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="LocalModelUnavailableException">No local model could answer; the message says why.</exception>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = await GetReadyClientAsync(cancellationToken);
        await using var enumerator = client.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            // C# does not allow yield inside a try with a catch, so each step is awaited in its own try.
            ChatResponseUpdate current;
            try
            {
                if (!await enumerator.MoveNextAsync()) yield break;
                current = enumerator.Current;
            }
            catch (Exception ex) when (IsRequestFailure(ex, cancellationToken))
            {
                throw new LocalModelUnavailableException(DescribeRequestFailure(ex), ex);
            }

            yield return current;
        }
    }

    /// <summary>Finds the service and loads the model, or throws with the reason neither worked.</summary>
    private async Task<IChatClient> GetReadyClientAsync(CancellationToken cancellationToken)
    {
        var status = await _localService.CheckAsync(ensureModelLoaded: true, cancellationToken);
        return status is { IsAvailable: true, Client: { } client }
            ? client
            : throw new LocalModelUnavailableException(status.Problem ?? "The local model service is unavailable.");
    }

    // A cancellation the caller asked for is not a failure; the SDK's network timeout surfaces as a cancellation too.
    private static bool IsRequestFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>Turns a failed model request into an actionable reason (timeouts and unknown models are the common cases).</summary>
    private string DescribeRequestFailure(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException =>
            $"Model '{ActiveModelId}' did not answer within {_options.Local.TimeoutSeconds}s. Increase Foundry:Local:TimeoutSeconds in appsettings.json or use a smaller or GPU model.",
        System.ClientModel.ClientResultException { Status: 404 } =>
            $"Foundry Local does not know model '{ActiveModelId}'. Check the name with 'foundry model list' and load it with 'foundry model run <model>'.",
        System.ClientModel.ClientResultException { Status: 400 } rejected =>
            $"Model '{ActiveModelId}' rejected the request (HTTP 400){ServerDetail(rejected)}. A document or question too long for the model's context window is the usual cause: " +
            "lower Foundry:Summarization:MaxSinglePassTokens or Foundry:Chat:MaxPassageTokens, or use a model with a larger context such as phi-4-mini.",
        _ => $"The request to model '{ActiveModelId}' failed: {ex.Message.Split('\n')[0].Trim()}"
    };

    /// <summary>
    /// The server's own error text. The SDK's message only shows the JSON "message" field, which Foundry Local
    /// often leaves empty, so the raw body is the only place the actual reason appears.
    /// </summary>
    private static string ServerDetail(System.ClientModel.ClientResultException ex)
    {
        string body;
        try
        {
            body = ex.GetRawResponse()?.Content?.ToString()?.Trim() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            body = string.Empty; // the response body was not buffered
        }

        if (body.Length == 0) return string.Empty;
        return $": {(body.Length <= 300 ? body : body[..300] + "…")}";
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc />
    public void Dispose() => _localService.Dispose();
}
