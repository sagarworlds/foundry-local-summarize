using System.Net.Http;
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

public class HybridChatClientRouter : IChatClient
{
    /// <summary>
    /// Prepended to any answer produced by <see cref="LocalFoundryFallbackClient"/>. That engine returns
    /// canned demo text that is not derived from the input, so it must never be mistaken for a real summary.
    /// </summary>
    public const string FallbackNotice =
        "> ⚠️ **Offline demo output.** No language model was reachable, so the text below is an illustrative template and was NOT generated from your document. Start Foundry Local (or check the endpoint/model in appsettings.json) and try again.\n\n";

    // Foundry Local can take well over 500 ms to answer /v1/models while a model is loading or
    // generating on CPU; a too-short probe silently diverted real requests to the demo engine.
    private const int HealthCheckTimeoutMs = 2000;

    private readonly FoundryOptions _options;
    private readonly LocalFoundryFallbackClient _fallbackClient = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private IChatClient? _localClient;
    private IChatClient? _cloudClient;

    public ChatClientMetadata Metadata => new("FoundryHybridRouter", new Uri(_options.LocalEndpoint), _options.LocalModelId);

    public RoutingDecisionInfo? LastRoutingDecision { get; private set; }

    public event Action<RoutingDecisionInfo>? OnRoutingDecision;

    public HybridChatClientRouter(FoundryOptions options)
    {
        _options = options;
        InitializeClients();
    }

    public void InitializeClients()
    {
        try
        {
            string localEndpoint = _options.GetEffectiveLocalEndpoint();
            var localOpenAi = new OpenAIClient(
                new System.ClientModel.ApiKeyCredential("local-foundry-key"),
                new OpenAIClientOptions { Endpoint = new Uri(localEndpoint) }
            );
            _localClient = localOpenAi.GetChatClient(_options.LocalModelId).AsIChatClient();
        }
        catch
        {
            _localClient = null;
        }

        if (!string.IsNullOrWhiteSpace(_options.CloudApiKey) && !string.IsNullOrWhiteSpace(_options.CloudEndpoint))
        {
            try
            {
                var cloudOpenAi = new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(_options.CloudApiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(_options.CloudEndpoint) }
                );
                _cloudClient = cloudOpenAi.GetChatClient(_options.CloudModelId).AsIChatClient();
            }
            catch
            {
                _cloudClient = null;
            }
        }
    }

    public async Task<bool> CheckLocalDaemonStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HealthCheckTimeoutMs);
            string endpoint = _options.GetEffectiveLocalEndpoint();
            var baseUri = new Uri(endpoint);
            var healthUrl = $"{baseUri.Scheme}://{baseUri.Authority}/v1/models";
            var resp = await _httpClient.GetAsync(healthUrl, cts.Token);
            if (resp.IsSuccessStatusCode && _localClient == null)
            {
                InitializeClients();
            }
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = chatMessages.ToList();
        var targetClient = await SelectClientAsync(list, cancellationToken);
        if (targetClient == _fallbackClient)
        {
            return WithFallbackNotice(await _fallbackClient.GetResponseAsync(list, options, cancellationToken));
        }

        try
        {
            return await targetClient.GetResponseAsync(list, options, cancellationToken);
        }
        catch (Exception ex) when (targetClient != _fallbackClient)
        {
            LastRoutingDecision = (LastRoutingDecision ?? new RoutingDecisionInfo("Fallback", _options.LocalEndpoint, _options.LocalModelId, true, true, 0, 0, "")) with
            {
                RouteName = "Local Offline Fallback",
                Rationale = $"Primary endpoint failed ({ex.Message}). Answered by the offline demo engine (output is not derived from the document)."
            };
            OnRoutingDecision?.Invoke(LastRoutingDecision);
            return WithFallbackNotice(await _fallbackClient.GetResponseAsync(list, options, cancellationToken));
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = chatMessages.ToList();
        var targetClient = await SelectClientAsync(list, cancellationToken);
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        bool failed = false;

        try
        {
            enumerator = targetClient.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch
        {
            failed = true;
        }

        if (failed || enumerator == null || targetClient == _fallbackClient)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, FallbackNotice);
            await foreach (var item in _fallbackClient.GetStreamingResponseAsync(list, options, cancellationToken))
            {
                yield return item;
            }
            yield break;
        }

        while (true)
        {
            ChatResponseUpdate current;
            try
            {
                if (!await enumerator.MoveNextAsync()) break;
                current = enumerator.Current;
            }
            catch
            {
                failed = true;
                break;
            }
            yield return current;
        }

        if (failed)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, FallbackNotice);
            await foreach (var item in _fallbackClient.GetStreamingResponseAsync(list, options, cancellationToken))
            {
                yield return item;
            }
        }
    }

    private async Task<IChatClient> SelectClientAsync(IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        int totalChars = messages.Sum(m => m.Text?.Length ?? 0);
        int estimatedTokens = (int)Math.Ceiling(totalChars / 4.0);

        if (_options.PrivacyMode)
        {
            bool isDaemonUp = await CheckLocalDaemonStatusAsync(cancellationToken);
            var client = (isDaemonUp && _localClient != null) ? _localClient : (IChatClient)_fallbackClient;

            LastRoutingDecision = new RoutingDecisionInfo(
                RouteName: isDaemonUp ? "Microsoft Foundry Local (Active Daemon)" : "Microsoft Foundry Local (Offline Fallback)",
                Endpoint: _options.LocalEndpoint,
                ModelId: _options.LocalModelId,
                IsLocal: true,
                IsPrivacyEnforced: true,
                EstimatedTokens: estimatedTokens,
                EstimatedCostUsd: 0.00m,
                Rationale: "🔒 Privacy Mode Active: Zero cloud egress. Data processed 100% locally on local hardware."
            );
            OnRoutingDecision?.Invoke(LastRoutingDecision);
            return client;
        }

        if (_options.EscalateOnComplexity && estimatedTokens > _options.EscalationTokenThreshold && _cloudClient != null)
        {
            decimal cost = Math.Round((decimal)(estimatedTokens * 0.000005 + 500 * 0.000015), 4);
            LastRoutingDecision = new RoutingDecisionInfo(
                RouteName: "Cloud Frontier Escalation (Azure AI Foundry)",
                Endpoint: _options.CloudEndpoint,
                ModelId: _options.CloudModelId,
                IsLocal: false,
                IsPrivacyEnforced: false,
                EstimatedTokens: estimatedTokens,
                EstimatedCostUsd: cost,
                Rationale: $"Document complexity ({estimatedTokens} tokens > {_options.EscalationTokenThreshold} threshold) escalated to Cloud Frontier model."
            );
            OnRoutingDecision?.Invoke(LastRoutingDecision);
            return _cloudClient;
        }

        bool daemonActive = await CheckLocalDaemonStatusAsync(cancellationToken);
        var chosenClient = (daemonActive && _localClient != null) ? _localClient : (IChatClient)_fallbackClient;

        LastRoutingDecision = new RoutingDecisionInfo(
            RouteName: daemonActive ? "Local Foundry ($0.00)" : "Local Offline Engine ($0.00)",
            Endpoint: _options.LocalEndpoint,
            ModelId: _options.LocalModelId,
            IsLocal: true,
            IsPrivacyEnforced: false,
            EstimatedTokens: estimatedTokens,
            EstimatedCostUsd: 0.00m,
            Rationale: $"Standard document size ({estimatedTokens} tokens) routed locally for maximum cost efficiency ($0.00)."
        );
        OnRoutingDecision?.Invoke(LastRoutingDecision);
        return chosenClient;
    }

    private static ChatResponse WithFallbackNotice(ChatResponse response) =>
        new(new ChatMessage(ChatRole.Assistant, FallbackNotice + response.Text))
        {
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt
        };

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        _localClient?.Dispose();
        _cloudClient?.Dispose();
        _httpClient.Dispose();
    }
}
