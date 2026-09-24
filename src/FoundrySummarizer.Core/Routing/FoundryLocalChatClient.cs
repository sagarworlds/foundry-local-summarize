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

/// <summary>Outcome of <see cref="FoundryLocalChatClient.SwitchModelAsync"/>.</summary>
/// <param name="Status">The new model's state; <see cref="LocalModelStatus.IsAvailable"/> is true once it is loaded.</param>
/// <param name="UnloadProblem">Why the previous model could not be unloaded, or null.</param>
public record ModelSwitchResult(LocalModelStatus Status, string? UnloadProblem);

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

    /// <summary>The model the user chose, or null when it is chosen automatically.</summary>
    public string? UserSelectedModelId => _localService.UserSelectedModelId;

    /// <summary>Lists the chat models downloaded on this machine (starting Foundry Local if needed).</summary>
    public Task<LocalModelList> ListModelsAsync(CancellationToken cancellationToken = default) =>
        _localService.ListModelsAsync(cancellationToken);

    /// <summary>Uses <paramref name="modelId"/> from now on; null returns to automatic selection.</summary>
    public void SelectModel(string? modelId) => _localService.SelectModel(modelId);

    /// <summary>
    /// Switches to <paramref name="modelId"/>: unloads the current model first (so both never compete for GPU
    /// memory), then loads the new one.
    /// </summary>
    /// <param name="modelId">A model id from <see cref="ListModelsAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the switch.</param>
    /// <returns>The new model's status, plus a warning if the previous model could not be unloaded.</returns>
    public async Task<ModelSwitchResult> SwitchModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var previous = ActiveModelId;
        string? unloadProblem = null;
        if (!FoundryLocalService.SameModel(previous, modelId))
        {
            unloadProblem = await _localService.UnloadModelAsync(previous, cancellationToken);
        }

        SelectModel(modelId);
        var status = await LoadActiveModelAsync(cancellationToken);
        return new ModelSwitchResult(status, unloadProblem);
    }

    /// <summary>Whether the active model is in Foundry Local's memory now; null when that cannot be determined.</summary>
    public Task<bool?> IsActiveModelLoadedAsync(CancellationToken cancellationToken = default) =>
        _localService.IsActiveModelLoadedAsync(cancellationToken);

    /// <summary>Loads the active model into memory now, so the first summary does not wait for it.</summary>
    public Task<LocalModelStatus> LoadActiveModelAsync(CancellationToken cancellationToken = default) =>
        _localService.CheckAsync(ensureModelLoaded: true, cancellationToken);

    /// <summary>Endpoint, model and (when unreachable) the reason, for status displays. Does not load a model.</summary>
    public Task<LocalModelStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _localService.CheckAsync(ensureModelLoaded: false, cancellationToken);

    /// <inheritdoc />
    /// <exception cref="LocalModelUnavailableException">No local model could answer; the message says why.</exception>
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();   // may be sent twice
        var client = await GetReadyClientAsync(cancellationToken);
        try
        {
            return await SendAsync(client, messageList, options, cancellationToken);
        }
        catch (System.ClientModel.ClientResultException ex) when (IsModelNotLoaded(ex))
        {
            // The model was unloaded between the check and the request (idle time-to-live): load it and retry once.
            if (await _localService.ReloadActiveModelAsync(cancellationToken) is { } loadProblem)
            {
                throw new LocalModelUnavailableException(loadProblem, ex);
            }
        }
        catch (Exception ex) when (IsRequestFailure(ex, cancellationToken))
        {
            throw new LocalModelUnavailableException(DescribeRequestFailure(ex), ex);
        }

        try
        {
            return await SendAsync(client, messageList, options, cancellationToken);
        }
        catch (Exception ex) when (IsRequestFailure(ex, cancellationToken))
        {
            throw new LocalModelUnavailableException(DescribeRequestFailure(ex), ex);
        }
    }

    /// <summary>
    /// Sends one request and returns only the answer: reasoning models are asked not to think where they support
    /// it, and any <c>&lt;think&gt;</c> reasoning they still write is removed (see <see cref="ReasoningOutputFilter"/>).
    /// </summary>
    /// <exception cref="LocalModelUnavailableException">The model produced reasoning but no answer.</exception>
    private async Task<ChatResponse> SendAsync(IChatClient client, IList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var response = await client.GetResponseAsync(ReasoningOutputFilter.SuppressThinking(ActiveModelId, messages), options, cancellationToken);

        var answer = ReasoningOutputFilter.RemoveReasoning(response.Text ?? string.Empty, out bool hadReasoning);
        if (!hadReasoning) return response;

        if (answer.Length == 0)
        {
            throw new LocalModelUnavailableException(
                $"Model '{ActiveModelId}' spent its whole answer on reasoning (<think>…</think>) and gave no answer. " +
                "Choose a model without built-in reasoning, such as phi-4-mini, from the Model list.");
        }

        response.Messages = new List<ChatMessage> { new(ChatRole.Assistant, answer) };
        return response;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Streams the raw output: unlike <see cref="GetResponseAsync"/>, reasoning (<c>&lt;think&gt;</c>) is not removed,
    /// because the app does not stream. Use <see cref="GetResponseAsync"/> for user-facing text.
    /// </remarks>
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
        System.ClientModel.ClientResultException { Status: 400 } rejected when IsModelNotLoaded(rejected) =>
            $"Foundry Local could not keep model '{ActiveModelId}' loaded. Load it with 'foundry model run {ActiveModelId}' and try again.",
        System.ClientModel.ClientResultException { Status: 400 } rejected when MentionsContextLength(rejected) =>
            $"The text is too long for model '{ActiveModelId}'{ServerDetail(rejected)}. Lower Foundry:Summarization:MaxSinglePassTokens " +
            "or Foundry:Chat:MaxPassageTokens in appsettings.json, or choose a model with a larger context window.",
        System.ClientModel.ClientResultException { Status: 400 } rejected =>
            $"Model '{ActiveModelId}' rejected the request (HTTP 400){ServerDetail(rejected)}",
        _ => $"The request to model '{ActiveModelId}' failed: {ex.Message.Split('\n')[0].Trim()}"
    };

    private static bool IsModelNotLoaded(System.ClientModel.ClientResultException ex) =>
        ex.Status == 400 && RawBody(ex).Contains("not loaded", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsContextLength(System.ClientModel.ClientResultException ex)
    {
        var body = RawBody(ex);
        return new[] { "context", "max length", "max_length", "too long", "exceeds" }
            .Any(term => body.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The server's own error text, shortened. The SDK's message only shows the JSON "message" field, which
    /// Foundry Local sometimes leaves empty, so the raw body is the only place the actual reason appears.
    /// </summary>
    private static string ServerDetail(System.ClientModel.ClientResultException ex)
    {
        var body = RawBody(ex);
        if (body.Length == 0) return string.Empty;
        return $": {(body.Length <= 300 ? body : body[..300] + "…")}";
    }

    private static string RawBody(System.ClientModel.ClientResultException ex)
    {
        try
        {
            return ex.GetRawResponse()?.Content?.ToString()?.Trim() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty; // the response body was not buffered
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc />
    public void Dispose() => _localService.Dispose();
}
