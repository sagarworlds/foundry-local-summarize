using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace FoundrySummarizer.Core.Grounding;

public interface IVectorGroundingService
{
    IReadOnlyList<GroundingRecord> IndexedPolicies { get; }
    Task IndexPolicyAsync(string title, string content, string category, string source, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroundingSearchResult>> SearchAsync(string queryText, int topK = 3, double minScore = 0.3, CancellationToken cancellationToken = default);
    Task<string> BuildGroundingPromptContextAsync(string documentText, CancellationToken cancellationToken = default);
}

public class VectorGroundingService : IVectorGroundingService
{
    private readonly List<GroundingRecord> _records = new();
    private readonly SemanticEmbeddingGenerator _embedder = new();
    private readonly object _lock = new();

    public IReadOnlyList<GroundingRecord> IndexedPolicies
    {
        get
        {
            lock (_lock) return _records.ToList();
        }
    }

    public VectorGroundingService()
    {
        SeedDefaultPolicies();
    }

    public Task IndexPolicyAsync(string title, string content, string category, string source, CancellationToken cancellationToken = default)
    {
        var vector = SemanticEmbeddingGenerator.CreateEmbedding($"{title} {category} {content}");
        var record = new GroundingRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Content = content,
            Category = category,
            DocumentSource = source,
            Vector = vector
        };

        lock (_lock)
        {
            _records.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GroundingSearchResult>> SearchAsync(string queryText, int topK = 3, double minScore = 0.25, CancellationToken cancellationToken = default)
    {
        var queryVec = SemanticEmbeddingGenerator.CreateEmbedding(queryText);
        var results = new List<GroundingSearchResult>();

        lock (_lock)
        {
            foreach (var rec in _records)
            {
                double score = SemanticEmbeddingGenerator.CosineSimilarity(queryVec, rec.Vector);
                if (score >= minScore)
                {
                    results.Add(new GroundingSearchResult(rec, score, rec.Content));
                }
            }
        }

        var ordered = results.OrderByDescending(r => r.SimilarityScore).Take(topK).ToList();
        return Task.FromResult<IReadOnlyList<GroundingSearchResult>>(ordered);
    }

    public async Task<string> BuildGroundingPromptContextAsync(string documentText, CancellationToken cancellationToken = default)
    {
        var matches = await SearchAsync(documentText, topK: 3, minScore: 0.25, cancellationToken);
        if (matches.Count == 0)
        {
            return string.Empty;
        }

        // The policies are wrapped in their own tag and explicitly marked as not being part of the
        // document. Small models otherwise blend policy text into the summary (e.g. reporting the
        // $100,000 policy threshold as if it were a cost in the document) or invent "deviations"
        // simply because they were told to flag some.
        var sb = new StringBuilder();
        sb.AppendLine("<reference_policies>");
        sb.AppendLine("=== SEMANTIC GROUNDING: CORPORATE POLICY CROSS-REFERENCES ===");
        sb.AppendLine("These internal policies are reference material only. They are NOT part of the document and their figures are NOT document facts.");
        sb.AppendLine();

        foreach (var match in matches)
        {
            sb.AppendLine($"[POLICY CITATION: {match.Record.Title} | Category: {match.Record.Category}]");
            sb.AppendLine(match.Record.Content.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("INSTRUCTION: Flag a deviation only when a specific figure or clause in the document actually violates one of these policies; quote the document value and the policy ID. If nothing in the document conflicts with a policy, do not mention that policy.");
        sb.AppendLine("</reference_policies>");

        return sb.ToString();
    }

    private void SeedDefaultPolicies()
    {
        IndexPolicyAsync(
            title: "Q2 Financial Framework - Capital Expenditure Thresholds",
            content: "Policy FIN-202: All departmental capital expenditures and hardware acquisitions exceeding $100,000 mandate formal written authorization from the Vice President of Finance. The quarterly ceiling for engineering modernization projects is capped at $350,000. Project managers must include verified ROI payback analysis for all equipment requests.",
            category: "Finance & Budgeting",
            source: "corporate_policies/Q2_Financial_Framework.txt"
        );

        IndexPolicyAsync(
            title: "Corporate Legal Contracting & Liability Cap Standards",
            content: "Policy LEG-104: Standard commercial agreements must enforce a mutual aggregate liability cap not exceeding 1x the total annual contract value, or 2x with General Counsel approval. Under no circumstances may uncapped consequential damages be agreed upon. Mutual indemnification for IP infringement and confidentiality is mandatory.",
            category: "Legal & Compliance",
            source: "corporate_policies/Legal_Contracting_Standards.txt"
        );

        IndexPolicyAsync(
            title: "Information Security & Data Sovereignty Directive",
            content: "Policy SEC-301: Enterprise data classified as Confidential or Internal-Only must not be routed through public or multi-tenant cloud inference endpoints without explicit CISO exemption. Local-first execution (Foundry Local on premises) is required for sensitive strategic, personnel, and contractual documents.",
            category: "Security & Privacy",
            source: "corporate_policies/Cloud_Compliance_Policy.txt"
        );
    }
}
