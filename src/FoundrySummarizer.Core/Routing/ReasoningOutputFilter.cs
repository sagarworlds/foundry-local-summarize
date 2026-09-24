using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Handles "reasoning" models (Qwen3, DeepSeek-R1, Phi-4-reasoning…), which write their chain of thought inside
/// <c>&lt;think&gt;…&lt;/think&gt;</c> before the answer. Users only want the answer, and a small model's thoughts
/// also contain guesses that must not end up in a summary or in the notes a summary is written from.
/// </summary>
public static class ReasoningOutputFilter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    /// <summary>Qwen3's documented soft switch that turns thinking off for a turn.</summary>
    public const string NoThinkDirective = "/no_think";

    private static readonly Regex ThinkBlock = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Removes the model's reasoning from <paramref name="text"/>: complete <c>&lt;think&gt;</c> blocks, a leading
    /// block whose opening tag was part of the prompt template (output starts mid-thought and has only
    /// <c>&lt;/think&gt;</c>), and an unfinished block cut off by the output limit.
    /// </summary>
    /// <param name="text">The raw model output.</param>
    /// <param name="hadReasoning">True when any reasoning was removed.</param>
    /// <returns>The answer alone, trimmed; empty when the output was only reasoning.</returns>
    public static string RemoveReasoning(string text, out bool hadReasoning)
    {
        hadReasoning = false;
        if (string.IsNullOrEmpty(text)) return string.Empty;

        int close = text.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
        int open = text.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
        if (close >= 0 && (open < 0 || open > close))
        {
            text = text[(close + CloseTag.Length)..];
            hadReasoning = true;
        }

        var withoutBlocks = ThinkBlock.Replace(text, string.Empty);
        hadReasoning |= withoutBlocks.Length != text.Length;
        text = withoutBlocks;

        open = text.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            text = text[..open];
            hadReasoning = true;
        }

        return text.Trim();
    }

    /// <summary>True for model families that accept <see cref="NoThinkDirective"/> (Qwen3).</summary>
    public static bool SupportsNoThinkDirective(string modelId) =>
        modelId.Contains("qwen3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For models that support it, appends <see cref="NoThinkDirective"/> to the last user message so the model
    /// answers directly instead of spending its output budget (and minutes on a CPU) on reasoning.
    /// </summary>
    /// <returns>The messages to send; the input list is never modified.</returns>
    public static IList<ChatMessage> SuppressThinking(string modelId, IList<ChatMessage> messages)
    {
        if (!SupportsNoThinkDirective(modelId)) return messages;

        int lastUser = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User) { lastUser = i; break; }
        }

        if (lastUser < 0 || (messages[lastUser].Text ?? string.Empty).TrimEnd().EndsWith(NoThinkDirective, StringComparison.Ordinal))
        {
            return messages;
        }

        var copy = messages.ToList();
        copy[lastUser] = new ChatMessage(ChatRole.User, $"{messages[lastUser].Text}\n\n{NoThinkDirective}");
        return copy;
    }
}
