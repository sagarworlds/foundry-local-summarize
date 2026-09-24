using Microsoft.Extensions.AI.Evaluation;

namespace FoundrySummarizer.Core.Evaluation;

/// <summary>
/// Text other than the source document that the model was legitimately given and may quote, such as the
/// persona prompt and matched company policies. Evaluators treat its facts as allowed, so a summary that
/// cites "Policy FIN-202" or the template's "Not stated" wording is not marked as inventing them.
/// </summary>
public sealed class ReferenceMaterialContext : EvaluationContext
{
    public const string ContextName = "Reference Material";

    /// <param name="text">Combined reference text.</param>
    public ReferenceMaterialContext(string text) : base(ContextName, text ?? string.Empty)
    {
        Text = text ?? string.Empty;
    }

    /// <summary>The reference text.</summary>
    public string Text { get; }
}
