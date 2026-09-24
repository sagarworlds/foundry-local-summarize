namespace FoundrySummarizer.Core.Routing;

public class LocalFoundryConfig
{
    /// <summary>
    /// Local endpoint used when auto-discovery is off or finds nothing, e.g. http://localhost:11434/v1 for Ollama.
    /// Foundry Local picks a new port each time its service starts, so for Foundry Local leave AutoDiscover on.
    /// </summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:63715/v1";

    /// <summary>
    /// Model identifier or alias (e.g., qwen2.5-0.5b-instruct-generic-cpu, qwen2.5-0.5b, phi-3.5-mini-instruct).
    /// </summary>
    public string ModelId { get; set; } = "qwen2.5-0.5b-instruct-generic-cpu";

    /// <summary>
    /// If true, finds the running Foundry Local service with <c>foundry service status</c> (as the official SDK does),
    /// re-discovering whenever the service stops answering; <see cref="Endpoint"/> is used only when that finds nothing.
    /// </summary>
    public bool AutoDiscover { get; set; } = true;

    /// <summary>
    /// Maximum seconds to wait for one model response. Small models on CPU can take minutes to summarize a
    /// long document; a request that times out is answered by the offline demo engine instead.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// When true and discovery finds the Foundry Local service stopped, the app runs <c>foundry service start</c>.
    /// </summary>
    public bool AutoStartService { get; set; } = true;

    /// <summary>Maximum seconds to wait for Foundry Local to load a model into memory before first use.</summary>
    public int ModelLoadTimeoutSeconds { get; set; } = 300;
    public string Provider { get; set; } = "FoundryLocal";

    /// <summary>
    /// When true, the router lists the models the local endpoint serves and uses the first match from
    /// <see cref="GetPreferredModels"/>, falling back to <see cref="ModelId"/>. Sub-1B models are too
    /// small to follow a structured summary template faithfully, so a stronger model is used when present.
    /// </summary>
    public bool AutoSelectModel { get; set; } = true;

    /// <summary>
    /// Ordered model aliases to prefer (best first). Each entry matches a served model id exactly or as a
    /// prefix, e.g. "phi-4-mini" matches "phi-4-mini-instruct-generic-gpu". Empty means use the built-in list.
    /// An array (not a List) so configuration binding replaces rather than appends to the defaults.
    /// </summary>
    public string[] PreferredModels { get; set; } = Array.Empty<string>();

    /// <summary>Returns <see cref="PreferredModels"/> if configured, otherwise the built-in preference order.</summary>
    public IReadOnlyList<string> GetPreferredModels() =>
        PreferredModels is { Length: > 0 } ? PreferredModels : LocalModelSelector.DefaultPreferences;
}

/// <summary>
/// Budgets for splitting long documents into parts before summarizing. Values are estimated tokens
/// (≈4 characters each) and should leave room for the prompt and the answer in the model's context window.
/// </summary>
public class SummarizationConfig
{
    /// <summary>Documents up to this size are summarized in a single model call.</summary>
    public int MaxSinglePassTokens { get; set; } = 2500;

    /// <summary>Size of each part when a document is too long for a single pass.</summary>
    public int MapChunkTokens { get; set; } = 1200;

    /// <summary>Overlap between consecutive parts so facts that straddle a boundary are not lost.</summary>
    public int MapChunkOverlapTokens { get; set; } = 60;

    /// <summary>Output budget for the notes extracted from each part.</summary>
    public int MapMaxOutputTokens { get; set; } = 450;

    /// <summary>
    /// Maximum passes of note-taking. If the combined notes are still over <see cref="MaxSinglePassTokens"/>,
    /// the notes themselves are condensed again, up to this many passes.
    /// </summary>
    public int MaxCondenseRounds { get; set; } = 3;
}

public class CloudFoundryConfig
{
    public string Endpoint { get; set; } = "https://models.inference.ai.azure.com";
    public string ModelId { get; set; } = "gpt-4o";
    public string? ApiKey { get; set; }
    public string Provider { get; set; } = "AzureAIFoundry";
}

/// <summary>
/// Context budgets for the interactive document chat, in estimated tokens. Together with the summary and
/// the answer they must fit the local model's context window.
/// </summary>
public class ChatConfig
{
    /// <summary>Budget for document passages sent with each question. Smaller documents are sent whole.</summary>
    public int MaxPassageTokens { get; set; } = 1500;

    /// <summary>Earlier question/answer pairs kept for follow-up questions (older turns are dropped).</summary>
    public int MaxHistoryTurns { get; set; } = 4;

    /// <summary>Output budget for each answer.</summary>
    public int MaxAnswerTokens { get; set; } = 500;
}

public class FoundryOptions
{
    public const string SectionName = "Foundry";

    public bool PrivacyMode { get; set; } = true;
    public bool EscalateOnComplexity { get; set; } = true;
    public int EscalationTokenThreshold { get; set; } = 2500;

    public LocalFoundryConfig Local { get; set; } = new();
    public CloudFoundryConfig Cloud { get; set; } = new();
    public SummarizationConfig Summarization { get; set; } = new();
    public ChatConfig Chat { get; set; } = new();

    public string GetEffectiveLocalEndpoint()
    {
        if (Local.AutoDiscover || string.Equals(Local.Endpoint, "auto", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string daemonPath = System.IO.Path.Combine(userProfile, ".foundry", "daemon.json");
                if (System.IO.File.Exists(daemonPath))
                {
                    string json = System.IO.File.ReadAllText(daemonPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("web_urls", out var urls) && urls.GetArrayLength() > 0)
                    {
                        var url = urls[0].GetString();
                        if (!string.IsNullOrEmpty(url))
                        {
                            return url.TrimEnd('/') + "/v1";
                        }
                    }
                }
            }
            catch
            {
                // Fall back
            }
        }

        if (string.Equals(Local.Endpoint, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "http://localhost:5272/v1";
        }

        return Local.Endpoint;
    }

    // Convenience accessors mapping to the sub-objects for backward compatibility and clean binding
    public string LocalEndpoint
    {
        get => Local.Endpoint;
        set => Local.Endpoint = value;
    }

    public string LocalModelId
    {
        get => Local.ModelId;
        set => Local.ModelId = value;
    }

    public string CloudEndpoint
    {
        get => Cloud.Endpoint;
        set => Cloud.Endpoint = value;
    }

    public string CloudModelId
    {
        get => Cloud.ModelId;
        set => Cloud.ModelId = value;
    }

    public string? CloudApiKey
    {
        get => Cloud.ApiKey;
        set => Cloud.ApiKey = value;
    }
}
