using Microsoft.Extensions.AI;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FoundrySummarizer.Core.Personas;

public interface IPromptyEngine
{
    IReadOnlyList<PromptyDocument> AvailablePersonas { get; }
    PromptyDocument Parse(string promptyContent);
    PromptyDocument? GetPersona(string name);
    void RegisterPersona(PromptyDocument document);
    IReadOnlyList<ChatMessage> RenderChatMessages(PromptyDocument document, IReadOnlyDictionary<string, string> variables);
}

public class PromptyEngine : IPromptyEngine
{
    private readonly Dictionary<string, PromptyDocument> _personas = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDeserializer _yamlDeserializer;

    public IReadOnlyList<PromptyDocument> AvailablePersonas => _personas.Values.ToList();

    public PromptyEngine()
    {
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        RegisterBuiltInPersonas();
    }

    public PromptyDocument Parse(string promptyContent)
    {
        if (string.IsNullOrWhiteSpace(promptyContent))
        {
            throw new ArgumentException("Prompty content cannot be empty", nameof(promptyContent));
        }

        var normalized = promptyContent.Replace("\r\n", "\n");
        string frontmatter = string.Empty;
        string body = normalized;

        if (normalized.StartsWith("---"))
        {
            int secondDelim = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (secondDelim >= 0)
            {
                frontmatter = normalized.Substring(3, secondDelim - 3).Trim();
                body = normalized.Substring(secondDelim + 4).Trim();
            }
        }

        var meta = !string.IsNullOrWhiteSpace(frontmatter)
            ? _yamlDeserializer.Deserialize<Dictionary<string, object>>(frontmatter)
            : new Dictionary<string, object>();

        string name = meta.TryGetValue("name", out var n) ? n?.ToString() ?? "Untitled" : "Untitled";
        string description = meta.TryGetValue("description", out var d) ? d?.ToString() ?? string.Empty : string.Empty;

        var modelConfig = new PromptyModelConfig();
        if (meta.TryGetValue("model", out var mObj) && mObj is Dictionary<object, object> mDict)
        {
            double temp = 0.2;
            int maxTok = 1500;
            if (mDict.TryGetValue("parameters", out var pObj) && pObj is Dictionary<object, object> pDict)
            {
                if (pDict.TryGetValue("temperature", out var tVal) && double.TryParse(tVal?.ToString(), out var parsedT)) temp = parsedT;
                if (pDict.TryGetValue("max_tokens", out var maxVal) && int.TryParse(maxVal?.ToString(), out var parsedM)) maxTok = parsedM;
            }
            modelConfig = new PromptyModelConfig(
                Api: mDict.TryGetValue("api", out var apiVal) ? apiVal?.ToString() : "chat",
                Temperature: temp,
                MaxTokens: maxTok
            );
        }

        string systemPrompt = string.Empty;
        string userPrompt = body;

        int systemIdx = body.IndexOf("system:", StringComparison.OrdinalIgnoreCase);
        int userIdx = body.IndexOf("user:", StringComparison.OrdinalIgnoreCase);

        if (systemIdx >= 0 && userIdx > systemIdx)
        {
            systemPrompt = body.Substring(systemIdx + 7, userIdx - (systemIdx + 7)).Trim();
            userPrompt = body.Substring(userIdx + 5).Trim();
        }
        else if (systemIdx >= 0 && userIdx < 0)
        {
            systemPrompt = body.Substring(systemIdx + 7).Trim();
            userPrompt = string.Empty;
        }

        return new PromptyDocument
        {
            Name = name,
            Description = description,
            ModelConfig = modelConfig,
            SystemPrompt = systemPrompt,
            UserPromptTemplate = userPrompt,
            RawContent = promptyContent
        };
    }

    public PromptyDocument? GetPersona(string name)
    {
        _personas.TryGetValue(name, out var doc);
        return doc;
    }

    public void RegisterPersona(PromptyDocument document)
    {
        _personas[document.Name] = document;
    }

    public IReadOnlyList<ChatMessage> RenderChatMessages(PromptyDocument document, IReadOnlyDictionary<string, string> variables)
    {
        var messages = new List<ChatMessage>();

        var system = document.RenderSystemPrompt(variables);
        if (!string.IsNullOrWhiteSpace(system))
        {
            messages.Add(new ChatMessage(ChatRole.System, system));
        }

        var user = document.RenderUserPrompt(variables);
        if (!string.IsNullOrWhiteSpace(user))
        {
            messages.Add(new ChatMessage(ChatRole.User, user));
        }

        return messages;
    }

    private void RegisterBuiltInPersonas()
    {
        RegisterPersona(Parse(ExecutiveBulletsPrompty));
        RegisterPersona(Parse(ActionItemExtractorPrompty));
        RegisterPersona(Parse(LegalComplianceCheckPrompty));
    }

    public static readonly string ExecutiveBulletsPrompty = """
    ---
    name: Executive Bullets
    description: High-level operational impact and cost figures for C-suite decision makers.
    model:
      api: chat
      parameters:
        temperature: 0.1
        max_tokens: 1500
    ---
    system:
    You are an elite Chief of Staff and Enterprise Intelligence Officer. Your output must strictly be high-level, actionable, and focused on strategic impact, financial figures, resource allocations, and critical risks. Do not include trivial technical minutiae.

    user:
    {{groundingContext}}

    DOCUMENT TO SUMMARIZE:
    {{documentText}}

    STRUCTURE YOUR SUMMARY ACCORDING TO THESE EXACT SECTIONS:
    ### 1. Executive Summary & Strategic Value
    - High-level synopsis of the core objective and organizational impact.

    ### 2. Financial & Cost Assessment
    - Explicit itemized and total cost figures.
    - Highlight any budget constraints, discrepancies, or policy caps noted.

    ### 3. Key Milestones & Critical Path
    - Major target dates, deliverables, and dependencies.

    ### 4. Strategic Risks & Recommended Executive Actions
    - Top operational or compliance risks and immediate decisions required.
    """;

    public static readonly string ActionItemExtractorPrompty = """
    ---
    name: Action-Item Extractor
    description: Technical task breakdowns mapped to owners, deadlines, and deliverables.
    model:
      api: chat
      parameters:
        temperature: 0.2
        max_tokens: 1500
    ---
    system:
    You are an Expert Project Management AI. Your objective is to extract and organize actionable tasks, ownership assignments, deadlines, technical requirements, and potential blockers from meeting transcripts or project documentation.

    user:
    {{groundingContext}}

    TRANSCRIPT / DOCUMENT CONTENT:
    {{documentText}}

    EXTRACT AND FORMAT ALL ACTION ITEMS AS FOLLOWS:
    ### Action Items Breakdown
    | # | Task Description | Assignee / Owner | Deadline / Priority | Deliverable / Success Criteria |
    |---|------------------|------------------|---------------------|--------------------------------|

    ### Key Technical Decisions Made
    - Bulleted list of confirmed technical choices, architectural agreements, or policy updates.

    ### Open Blockers & Dependencies
    - Any unresolved dependencies, missing approvals, or cross-team prerequisites.
    """;

    public static readonly string LegalComplianceCheckPrompty = """
    ---
    name: Legal Compliance Check
    description: Specific clauses, indemnification limits, and compliance risk highlights from contracts.
    model:
      api: chat
      parameters:
        temperature: 0.1
        max_tokens: 1800
    ---
    system:
    You are a Senior Corporate Legal and Regulatory Counsel Assistant. Your mission is to analyze commercial agreements, vendor contracts, and policy documents to identify liability exposures, indemnification obligations, termination terms, and compliance vulnerabilities.

    user:
    {{groundingContext}}

    CONTRACT / POLICY DOCUMENT:
    {{documentText}}

    PERFORM COMPREHENSIVE LEGAL ASSESSMENT:
    ### 1. Contract Overview & Commercial Terms
    - Parties involved, contract term, governing law, and core transaction value.

    ### 2. High-Risk Clauses & Liability Exposures
    - Liability Caps: (Verify against company standard policy of 1x or 2x contract value)
    - Indemnification & Intellectual Property protection clauses.
    - Data privacy, GDPR/HIPAA/SOC2 compliance requirements.

    ### 3. Termination, Cure Periods & Breach Consequences
    - Notice periods required for convenience and cause.

    ### 4. Red Flag Warnings & Counsel Recommendations
    - Specific clauses requiring renegotiation or executive exception approval before signature.
    """;
}
