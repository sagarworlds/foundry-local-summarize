using Microsoft.Extensions.AI;

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

    /// <summary>
    /// Translates the persona's frontmatter sampling parameters into <see cref="ChatOptions"/>.
    /// Without this, the endpoint's own defaults apply (often temperature ~0.7-1.0 and a short
    /// output cap), which makes small local models drift from the source text or truncate sections.
    /// </summary>
    /// <returns>Chat options carrying temperature, top-p and the max output token budget.</returns>
    public ChatOptions ToChatOptions() => new()
    {
        Temperature = (float)ModelConfig.Temperature,
        TopP = (float)ModelConfig.TopP,
        MaxOutputTokens = ModelConfig.MaxTokens
    };

    public string RenderUserPrompt(IReadOnlyDictionary<string, string> variables) =>
        RenderTemplate(UserPromptTemplate, variables);

    public string RenderSystemPrompt(IReadOnlyDictionary<string, string> variables) =>
        RenderTemplate(SystemPrompt, variables);

    /// <summary>
    /// Substitutes <c>{{name}}</c> placeholders in a single pass. Sequential string replacement would
    /// re-scan already-inserted values, so a document that happens to contain "{{groundingContext}}"
    /// would get the policy block injected into the middle of the source text.
    /// </summary>
    private static string RenderTemplate(string template, IReadOnlyDictionary<string, string> variables) =>
        PlaceholderRegex.Replace(template, m =>
            variables.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

    private static readonly System.Text.RegularExpressions.Regex PlaceholderRegex =
        new(@"\{\{\s*(\w+)\s*\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);
}
