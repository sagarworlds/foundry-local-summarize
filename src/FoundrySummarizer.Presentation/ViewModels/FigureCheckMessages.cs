using FoundrySummarizer.Core.Verification;

namespace FoundrySummarizer.Presentation.ViewModels;

/// <summary>Wording of figure-check results, shared by the summary and chat screens.</summary>
public static class FigureCheckMessages
{
    /// <summary>
    /// A warning for figures not found in the document, e.g. "⚠️ 2 of 9 figures do not appear in the document:
    /// $165,000, 15%. They may be calculated or invented; check them before relying on the summary."
    /// </summary>
    /// <param name="check">A result with at least one figure not found.</param>
    /// <param name="subject">What the figures are in, e.g. "summary" or "answer".</param>
    public static string Warning(FigureCheckResult check, string subject)
    {
        ArgumentNullException.ThrowIfNull(check);
        var list = string.Join(", ", check.NotFound.Select(f => f.Text));
        return check.NotFound.Count == 1
            ? $"⚠️ 1 of {check.Checked.Count} figures does not appear in the document: {list}. It may be calculated or invented; check it before relying on the {subject}."
            : $"⚠️ {check.NotFound.Count} of {check.Checked.Count} figures do not appear in the document: {list}. They may be calculated or invented; check them before relying on the {subject}.";
    }

    /// <summary>A confirmation that every figure was found, e.g. "✓ All 9 figures in the summary … appear in the document."</summary>
    /// <param name="check">A result whose figures were all found.</param>
    /// <param name="subject">What the figures are in, e.g. "summary".</param>
    public static string Confirmation(FigureCheckResult check, string subject)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Checked.Count == 1
            ? $"✓ The 1 figure in the {subject} appears in the document."
            : $"✓ All {check.Checked.Count} figures in the {subject} (amounts, percentages, dates, periods, numbers) appear in the document.";
    }
}
