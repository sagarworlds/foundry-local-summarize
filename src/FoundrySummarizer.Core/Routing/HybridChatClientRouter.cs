using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;

namespace FoundrySummarizer.Core.Routing;

public record RoutingDecisionInfo(
    string RouteName,
    string Endpoint,
    string ModelId,
    bool IsLocal,
    bool IsPrivacyEnforced,
    int EstimatedTokens,
    decimal EstimatedCostUsd,
    string Rationale
);

/// <summary>
/// Routes chat requests to the local model (Foundry Local), the cloud model for large documents when privacy mode
/// is off, or — when no model can answer — the offline demo engine, whose output is always labelled with the reason.
/// </summary>
public class HybridChatClientRouter : IChatClient
{
    /// <summary>
    /// Prepended to any answer produced by <see cref="LocalFoundryFallbackClient"/>. That engine returns
    /// canned demo text that is not derived from the input, so it must never be mistaken for a real summary.
    /// A "Why:" line with the specific cause follows it.
    /// </summary>
    public const string FallbackNotice =
        "> ⚠️ **Offline demo output.** No language model was reachable, so the text below is an illustrative template and was NOT generated from your document. Start Foundry Local (or check the endpoint/model in appsettings.json) and try again.\n\n";

    private readonly FoundryOptions _options;
    private readonly FoundryLocalService _localService;
    private readonly LocalFoundryFallbackClient _fallbackClient = new();
    private IChatClient? _cloudClient;

    public ChatClientMetadata Metadata => new("FoundryHybridRouter", ActiveLocalEndpoint, ActiveLocalModelId);

    /// <summary>
    /// The local model requests are sent to. Starts as the configured <c>Local.ModelId</c> and, when
    /// <c>Local.AutoSelectModel</c> is on, is upgraded to the best preferred model the service offers.
    /// </summary>
    public string ActiveLocalModelId => _localService.ActiveModelId;

    /// <summary>The local endpoint in use: the discovered Foundry Local address once found, else the configured one.</summary>
    public Uri ActiveLocalEndpoint => _localService.Endpoint ?? new Uri(_options.GetEffectiveLocalEndpoint());

    /// <summary>Why the last request was answered by the offline demo engine; null if it was not.</summary>
    public string? LastFallbackReason { get; private set; }

    public RoutingDecisionInfo? LastRoutingDecision { get; private set; }

    public event Action<RoutingDecisionInfo>? OnRoutingDecision;

    /// <param name="options">Routing, local and cloud settings.</param>
    /// <param name="localService">Local service manager; created from <paramref name="options"/> when omitted.</param>
    public HybridChatClientRouter(FoundryOptions options, FoundryLocalService? localService = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _localService = localService ?? new FoundryLocalService(options);
        InitializeClients();
    }

    /// <summary>(Re)creates the cloud client from the current options. Local clients are managed by the local service.</summary>
    public void InitializeClients()
    {
        _cloudClient = null;
        if (string.IsNullOrWhiteSpace(_options.CloudApiKey) || string.IsNullOrWhiteSpace(_options.CloudEndpoint))
        {
            return;
        }

        try
        {
            var cloudOpenAi = new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(_options.CloudApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(_options.CloudEndpoint) }
            );
            _cloudClient = cloudOpenAi.GetChatClient(_options.CloudModelId).AsIChatClient();
        }
        catch (UriFormatException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HybridChatClientRouter] Cloud endpoint '{_options.CloudEndpoint}' is invalid; cloud escalation disabled: {ex.Message}");
        }
    }

    /// <summary>Quick reachability check for status displays; does not load a model.</summary>
    public async Task<bool> CheckLocalDaemonStatusAsync(CancellationToken cancellationToken = default) =>
        (await GetLocalStatusAsync(cancellationToken)).IsAvailable;

    /// <summary>Endpoint, model and — when unreachable — the reason, for status displays. Does not load a model.</summary>
    public Task<LocalModelStatus> GetLocalStatusAsync(CancellationToken cancellationToken = default) =>
        _localService.CheckAsync(ensureModelLoaded: false, cancellationToken);

    /// <summary>
    /// Whether a request of <paramref name="estimatedTokens"/> would be escalated to the cloud model.
    /// Callers use this to skip local-only strategies (such as splitting a long document into parts)
    /// when the large-context cloud model will receive the request anyway.
    /// </summary>
    public bool WouldEscalateToCloud(int estimatedTokens) =>
        !_options.PrivacyMode &&
        _options.EscalateOnComplexity &&
        estimatedTokens > _options.EscalationTokenThreshold &&
        _cloudClient != null;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = chatMessages.ToList();
        var (targetClient, unavailableReason) = await SelectClientAsync(list, cancellationToken);
        if (targetClient == _fallbackClient)
        {
            return WithFallbackNotice(await _fallbackClient.GetResponseAsync(list, options, cancellationToken), unavailableReason);
        }

        try
        {
            LastFallbackReason = null;
            return await targetClient.GetResponseAsync(list, options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var reason = DescribeRequestFailure(ex);
            RecordFallback(reason);
            return WithFallbackNotice(await _fallbackClient.GetResponseAsync(list, options, cancellationToken), reason);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = chatMessages.ToList();
        var (targetClient, reason) = await SelectClientAsync(list, cancellationToken);
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;

        if (targetClient != _fallbackClient)
        {
            try
            {
                enumerator = targetClient.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reason = DescribeRequestFailure(ex);
            }
        }

        if (enumerator is not null)
        {
            await using (enumerator)
            {
                while (true)
                {
                    ChatResponseUpdate current;
                    try
                    {
                        if (!await enumerator.MoveNextAsync()) yield break;
                        current = enumerator.Current;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        reason = DescribeRequestFailure(ex);
                        break;
                    }
                    yield return current;
                }
            }
        }

        RecordFallback(reason ?? "The local model did not respond.");
        yield return new ChatResponseUpdate(ChatRole.Assistant, FallbackNotice + FormatReason(LastFallbackReason!));
        await foreach (var item in _fallbackClient.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            yield return item;
        }
    }

    /// <returns>The client to use and, when it is the demo engine, why no model was available.</returns>
    private async Task<(IChatClient Client, string? UnavailableReason)> SelectClientAsync(IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        int totalChars = messages.Sum(m => m.Text?.Length ?? 0);
        int estimatedTokens = (int)Math.Ceiling(totalChars / 4.0);

        if (!_options.PrivacyMode && WouldEscalateToCloud(estimatedTokens) && _cloudClient is { } cloudClient)
        {
            decimal cost = Math.Round((decimal)(estimatedTokens * 0.000005 + 500 * 0.000015), 4);
            Publish(new RoutingDecisionInfo(
                RouteName: "Cloud Frontier Escalation (Azure AI Foundry)",
                Endpoint: _options.CloudEndpoint,
                ModelId: _options.CloudModelId,
                IsLocal: false,
                IsPrivacyEnforced: false,
                EstimatedTokens: estimatedTokens,
                EstimatedCostUsd: cost,
                Rationale: $"Document complexity ({estimatedTokens} tokens > {_options.EscalationTokenThreshold} threshold) escalated to Cloud Frontier model."));
            return (cloudClient, null);
        }

        var status = await _localService.CheckAsync(ensureModelLoaded: true, cancellationToken);
        string privacyNote = _options.PrivacyMode ? "🔒 Privacy Mode Active: zero cloud egress. " : string.Empty;

        if (status.IsAvailable && status.Client is not null)
        {
            Publish(new RoutingDecisionInfo(
                RouteName: _options.PrivacyMode ? "Microsoft Foundry Local (Privacy Mode)" : "Local Foundry ($0.00)",
                Endpoint: status.Endpoint?.ToString() ?? _options.LocalEndpoint,
                ModelId: status.ModelId,
                IsLocal: true,
                IsPrivacyEnforced: _options.PrivacyMode,
                EstimatedTokens: estimatedTokens,
                EstimatedCostUsd: 0.00m,
                Rationale: $"{privacyNote}Processed locally by '{status.ModelId}' at {status.Endpoint}."));
            return (status.Client, null);
        }

        var reason = status.Problem ?? "The local model service is unavailable.";
        RecordFallback(reason);
        return (_fallbackClient, reason);
    }

    private void RecordFallback(string reason)
    {
        LastFallbackReason = reason;
        Publish(new RoutingDecisionInfo(
            RouteName: "Offline Demo Engine (no model reachable)",
            Endpoint: ActiveLocalEndpoint.ToString(),
            ModelId: ActiveLocalModelId,
            IsLocal: true,
            IsPrivacyEnforced: _options.PrivacyMode,
            EstimatedTokens: 0,
            EstimatedCostUsd: 0.00m,
            Rationale: $"{(_options.PrivacyMode ? "🔒 Privacy Mode Active: zero cloud egress. " : "")}⚠️ {reason} The output is canned demo text, not generated from the document."));
    }

    private void Publish(RoutingDecisionInfo decision)
    {
        LastRoutingDecision = decision;
        OnRoutingDecision?.Invoke(decision);
    }

    /// <summary>Turns a failed model request into an actionable reason (timeouts and unknown models are the common cases).</summary>
    private string DescribeRequestFailure(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException =>
            $"Model '{ActiveLocalModelId}' did not answer within {_options.Local.TimeoutSeconds}s. Increase Foundry:Local:TimeoutSeconds or use a smaller or GPU model.",
        System.ClientModel.ClientResultException { Status: 404 } =>
            $"The service does not know model '{ActiveLocalModelId}'. Check the name with 'foundry model list' and load it with 'foundry model run <model>'.",
        _ => $"The request to model '{ActiveLocalModelId}' failed: {ex.Message.Split('\n')[0].Trim()}"
    };

    private static string FormatReason(string reason) => $"> **Why:** {reason}\n\n";

    private ChatResponse WithFallbackNotice(ChatResponse response, string? reason) =>
        new(new ChatMessage(ChatRole.Assistant, FallbackNotice + FormatReason(reason ?? LastFallbackReason ?? "No model was reachable.") + response.Text))
        {
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt
        };

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        _localService.Dispose();
        _cloudClient?.Dispose();
    }
}
