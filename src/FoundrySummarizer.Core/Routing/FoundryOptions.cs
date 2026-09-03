namespace FoundrySummarizer.Core.Routing;

public class LocalFoundryConfig
{
    /// <summary>
    /// Local Foundry endpoint. Set to "auto" to automatically read the active daemon port from ~/.foundry/daemon.json, or provide an explicit URL (e.g. http://127.0.0.1:63715/v1 or http://localhost:11434/v1 for Ollama).
    /// </summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:63715/v1";

    /// <summary>
    /// Model identifier or alias (e.g., qwen2.5-0.5b-instruct-generic-cpu, qwen2.5-0.5b, phi-3.5-mini-instruct).
    /// </summary>
    public string ModelId { get; set; } = "qwen2.5-0.5b-instruct-generic-cpu";

    /// <summary>
    /// If true, automatically queries ~/.foundry/daemon.json for the active Foundry Local URL if Endpoint is "auto" or unreachable.
    /// </summary>
    public bool AutoDiscover { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 15;
    public string Provider { get; set; } = "FoundryLocal";
}

public class CloudFoundryConfig
{
    public string Endpoint { get; set; } = "https://models.inference.ai.azure.com";
    public string ModelId { get; set; } = "gpt-4o";
    public string? ApiKey { get; set; }
    public string Provider { get; set; } = "AzureAIFoundry";
}

public class FoundryOptions
{
    public const string SectionName = "Foundry";

    public bool PrivacyMode { get; set; } = true;
    public bool EscalateOnComplexity { get; set; } = true;
    public int EscalationTokenThreshold { get; set; } = 2500;

    public LocalFoundryConfig Local { get; set; } = new();
    public CloudFoundryConfig Cloud { get; set; } = new();

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
