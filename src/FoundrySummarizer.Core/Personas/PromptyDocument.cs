namespace FoundrySummarizer.Core.Personas;

public record PromptyModelConfig(
    string? Api = "chat",
    string? ModelName = null,
    double Temperature = 0.2,
    int MaxTokens = 1500,
    double TopP = 0.95
);

public record PromptyDocument
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public PromptyModelConfig ModelConfig { get; init; } = new();
    public Dictionary<string, object> Inputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string SystemPrompt { get; init; } = string.Empty;
    public string UserPromptTemplate { get; init; } = string.Empty;
    public string RawContent { get; init; } = string.Empty;

    public string RenderUserPrompt(IReadOnlyDictionary<string, string> variables)
    {
        var rendered = UserPromptTemplate;
        foreach (var (key, value) in variables)
        {
            rendered = rendered.Replace("{{" + key + "}}", value);
            rendered = rendered.Replace("{{ " + key + " }}", value);
        }
        return rendered;
    }

    public string RenderSystemPrompt(IReadOnlyDictionary<string, string> variables)
    {
        var rendered = SystemPrompt;
        foreach (var (key, value) in variables)
        {
            rendered = rendered.Replace("{{" + key + "}}", value);
            rendered = rendered.Replace("{{ " + key + " }}", value);
        }
        return rendered;
    }
}
