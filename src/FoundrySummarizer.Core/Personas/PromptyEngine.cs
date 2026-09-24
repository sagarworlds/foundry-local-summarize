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
            double topP = 0.95;
            if (mDict.TryGetValue("parameters", out var pObj) && pObj is Dictionary<object, object> pDict)
            {
                // Invariant culture: YAML always uses '.' decimals; under e.g. de-DE "0.1" would otherwise parse as 1.
                if (pDict.TryGetValue("temperature", out var tVal) && double.TryParse(tVal?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedT)) temp = parsedT;
                if (pDict.TryGetValue("max_tokens", out var maxVal) && int.TryParse(maxVal?.ToString(), out var parsedM)) maxTok = parsedM;
                if (pDict.TryGetValue("top_p", out var pVal) && double.TryParse(pVal?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedP)) topP = parsedP;
            }
            modelConfig = new PromptyModelConfig(
                Api: mDict.TryGetValue("api", out var apiVal) ? apiVal?.ToString() : "chat",
                Temperature: temp,
                MaxTokens: maxTok,
                TopP: topP
            );
        }

        string systemPrompt = string.Empty;
        string userPrompt = body;

        // Role markers are matched only at the start of a line so that prose such as
        // "the end user: ..." inside a prompt is not mistaken for a role boundary.
        var systemMatch = RoleMarkerRegex("system").Match(body);
        var userMatch = RoleMarkerRegex("user").Match(body);

        if (systemMatch.Success && userMatch.Success && userMatch.Index > systemMatch.Index)
        {
            int systemStart = systemMatch.Index + systemMatch.Length;
            systemPrompt = body.Substring(systemStart, userMatch.Index - systemStart).Trim();
            userPrompt = body.Substring(userMatch.Index + userMatch.Length).Trim();
        }
        else if (systemMatch.Success && !userMatch.Success)
        {
            systemPrompt = body.Substring(systemMatch.Index + systemMatch.Length).Trim();
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

    private static System.Text.RegularExpressions.Regex RoleMarkerRegex(string role) =>
        new($@"^[ \t]*{role}:", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

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
        top_p: 0.9
        max_tokens: 1500
    ---
    system:
    You are an elite Chief of Staff and Enterprise Intelligence Officer. Your output must strictly be high-level, actionable, and focused on strategic impact, financial figures, resource allocations, and critical risks. Do not include trivial technical minutiae.

    ACCURACY RULES (follow strictly):
    1. Use ONLY facts stated inside <document>. Never invent names, dates, amounts, parties, clauses or deadlines.
    2. If a section asks for information the document does not contain, write "Not stated in the document." for that item.
    3. Copy figures, dates and names exactly as written in the document. Do not round, convert or estimate.
    4. The text in [square brackets] below describes what to write. Replace it with real content; never copy it into the answer.

    user:
    <document>
    {{documentText}}
    </document>

    Summarize the document above using EXACTLY these four sections and headings:

    ### 1. Executive Summary & Strategic Value
    - [2-3 bullets: the core objective and its organizational impact, as stated in the document]

    ### 2. Financial & Cost Assessment
    - [Each cost figure from the document with what it pays for, then the total if the document gives one]
    - [Budget constraints or discrepancies stated in the document]

    ### 3. Key Milestones & Critical Path
    - [Target dates, deliverables and dependencies exactly as stated]

    ### 4. Strategic Risks & Recommended Executive Actions
    - [Risks the document raises and the decisions it asks for]
    """;

    public static readonly string ActionItemExtractorPrompty = """
    ---
    name: Action-Item Extractor
    description: Technical task breakdowns mapped to owners, deadlines, and deliverables.
    model:
      api: chat
      parameters:
        temperature: 0.1
        top_p: 0.9
        max_tokens: 1500
    ---
    system:
    You are an Expert Project Management AI. Your objective is to extract and organize actionable tasks, ownership assignments, deadlines, technical requirements, and potential blockers from meeting transcripts or project documentation.

    ACCURACY RULES (follow strictly):
    1. Use ONLY facts stated inside <document>. Never invent names, dates, amounts, parties, clauses or deadlines.
    2. If a section asks for information the document does not contain, write "Not stated in the document." for that item.
    3. Copy figures, dates and names exactly as written in the document. Do not round, convert or estimate.
    4. The text in [square brackets] below describes what to write. Replace it with real content; never copy it into the answer.
    5. List a task only if the document states or clearly assigns it. If no owner or deadline is given, write "Unassigned" or "Not stated" in that cell.

    user:
    <document>
    {{documentText}}
    </document>

    Extract every action item from the document above using EXACTLY this format:

    ### Action Items Breakdown
    | # | Task Description | Assignee / Owner | Deadline / Priority | Deliverable / Success Criteria |
    |---|------------------|------------------|---------------------|--------------------------------|
    [One row per task found in the document]

    ### Key Technical Decisions Made
    - [Decisions, architectural agreements or policy updates the document confirms]

    ### Open Blockers & Dependencies
    - [Unresolved dependencies, missing approvals or cross-team prerequisites the document mentions]
    """;

    public static readonly string LegalComplianceCheckPrompty = """
    ---
    name: Legal Compliance Check
    description: Specific clauses, indemnification limits, and compliance risk highlights from contracts.
    model:
      api: chat
      parameters:
        temperature: 0.1
        top_p: 0.9
        max_tokens: 1800
    ---
    system:
    You are a Senior Corporate Legal and Regulatory Counsel Assistant. Your mission is to analyze commercial agreements, vendor contracts, and policy documents to identify liability exposures, indemnification obligations, termination terms, and compliance vulnerabilities.

    ACCURACY RULES (follow strictly):
    1. Use ONLY facts stated inside <document>. Never invent names, dates, amounts, parties, clauses or deadlines.
    2. If a section asks for information the document does not contain, write "Not stated in the document." for that item.
    3. Copy figures, dates and names exactly as written in the document. Do not round, convert or estimate.
    4. The text in [square brackets] below describes what to write. Replace it with real content; never copy it into the answer.
    5. When you describe a clause, cite its section number or quote its key words from the document.

    user:
    <document>
    {{documentText}}
    </document>

    Assess the document above using EXACTLY these four sections and headings:

    ### 1. Contract Overview & Commercial Terms
    - [Parties, contract term, governing law and transaction value as stated]

    ### 2. High-Risk Clauses & Liability Exposures
    - Liability Caps: [the cap as written, and any limit relative to contract value]
    - Indemnification & IP: [the indemnification and intellectual property clauses as written]
    - Data Privacy & Compliance: [GDPR/HIPAA/SOC2 or other obligations the document names]

    ### 3. Termination, Cure Periods & Breach Consequences
    - [Notice periods for convenience and for cause, and cure periods, as written]

    ### 4. Red Flag Warnings & Counsel Recommendations
    - [Clauses needing renegotiation or executive exception approval, each with the reason]
    """;
}
