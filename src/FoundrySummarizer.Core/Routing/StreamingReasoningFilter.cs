using System.Text;

namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// The streaming counterpart of <see cref="ReasoningOutputFilter.RemoveReasoning"/>. It is fed the model's output
/// piece by piece and passes the answer through as it arrives, while holding back reasoning
/// (<c>&lt;think&gt;…&lt;/think&gt;</c>), so the user watches the answer being written without the model's thoughts
/// ever appearing on screen. One instance filters one response.
/// </summary>
public sealed class StreamingReasoningFilter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private enum State
    {
        // Nothing decided yet: the output may still turn out to start with <think>.
        Start,
        // Inside reasoning; everything is held until </think>.
        Thinking,
        // Passing the answer through.
        Answer,
        // Reasoning started again after the answer (cut off by the output limit); the rest is dropped.
        Done
    }

    private readonly StringBuilder _pending = new();
    private readonly bool _startsInsideReasoning;
    private State _state;
    private bool _openedExplicitly;

    /// <param name="modelId">The model writing the output; decides whether it starts inside a reasoning block.</param>
    public StreamingReasoningFilter(string modelId)
    {
        // These models' chat templates open the <think> block themselves, so their output starts mid-thought and
        // only the closing tag appears. Holding everything until it does keeps the thoughts off screen.
        _startsInsideReasoning = ReasoningOutputFilter.StartsInsideReasoning(modelId ?? string.Empty);
        _state = _startsInsideReasoning ? State.Thinking : State.Start;
    }

    /// <summary>True once any reasoning was seen (and held back).</summary>
    public bool HadReasoning { get; private set; }

    /// <summary>True once any answer text (other than white space) was passed through.</summary>
    public bool HasAnswer { get; private set; }

    /// <summary>Adds the next piece of output.</summary>
    /// <param name="delta">Text the model just wrote; null or empty is ignored.</param>
    /// <returns>Answer text that can be shown now (possibly empty).</returns>
    public string Push(string? delta)
    {
        if (!string.IsNullOrEmpty(delta)) _pending.Append(delta);
        return Drain(isComplete: false);
    }

    /// <summary>Ends the response.</summary>
    /// <returns>Answer text that was still held back, e.g. a trailing "&lt;" that turned out not to start a tag.</returns>
    public string Complete() => Drain(isComplete: true);

    private string Drain(bool isComplete)
    {
        var output = new StringBuilder();
        while (true)
        {
            var text = _pending.ToString();
            switch (_state)
            {
                case State.Start:
                {
                    var trimmed = text.TrimStart();
                    if (trimmed.StartsWith(OpenTag, StringComparison.OrdinalIgnoreCase))
                    {
                        _pending.Clear().Append(trimmed[OpenTag.Length..]);
                        _openedExplicitly = true;
                        HadReasoning = true;
                        _state = State.Thinking;
                        continue;
                    }

                    // Too short to tell whether "<thi…" becomes <think>: wait for more, unless the output has ended.
                    if (!isComplete && (trimmed.Length == 0 || OpenTag.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)))
                    {
                        return output.ToString();
                    }

                    _state = State.Answer;
                    continue;
                }

                case State.Thinking:
                {
                    int close = text.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (close >= 0)
                    {
                        HadReasoning = true;
                        _pending.Clear().Append(text[(close + CloseTag.Length)..]);
                        _state = State.Answer;
                        continue;
                    }

                    if (isComplete)
                    {
                        // Ended without </think>. After an explicit <think> that is reasoning cut off by the output
                        // limit (nothing to show). For a model that "starts inside reasoning" but never closed it,
                        // there was no reasoning at all: the whole output is the answer, as RemoveReasoning returns it.
                        bool wasReasoning = _openedExplicitly || text.TrimStart().StartsWith(OpenTag, StringComparison.OrdinalIgnoreCase);
                        _pending.Clear();
                        if (wasReasoning)
                        {
                            HadReasoning = true;
                            _state = State.Done;
                            return output.ToString();
                        }

                        Emit(output, text);
                        _state = State.Done;
                    }

                    return output.ToString();
                }

                case State.Answer:
                {
                    int open = text.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
                    int close = text.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (open >= 0 && (close < 0 || open < close))
                    {
                        // Reasoning after the answer is always cut off by the output limit; keep the answer only.
                        Emit(output, text[..open]);
                        HadReasoning = true;
                        _pending.Clear();
                        _state = State.Done;
                        return output.ToString();
                    }

                    if (close >= 0)
                    {
                        // A stray closing tag in the middle of an answer: the text before it was already shown and
                        // cannot be taken back, so only the tag itself is dropped.
                        Emit(output, text[..close]);
                        HadReasoning = true;
                        _pending.Clear().Append(text[(close + CloseTag.Length)..]);
                        continue;
                    }

                    // Hold back a trailing "<", "</thi"… that may become a tag with the next piece.
                    int hold = isComplete ? 0 : PartialTagLength(text);
                    Emit(output, text[..^hold]);
                    _pending.Clear().Append(text[^hold..]);
                    return output.ToString();
                }

                default:
                    _pending.Clear();
                    return output.ToString();
            }
        }
    }

    private void Emit(StringBuilder output, string text)
    {
        // The answer is trimmed at the start, like RemoveReasoning's result (the model usually writes "\n\n"
        // after </think>). Trailing white space is kept: more text may follow it.
        if (!HasAnswer) text = text.TrimStart();
        if (text.Length == 0) return;
        output.Append(text);
        HasAnswer = true;
    }

    /// <summary>Length of the longest end of <paramref name="text"/> that is the start of a tag.</summary>
    private static int PartialTagLength(string text)
    {
        for (int length = Math.Min(text.Length, CloseTag.Length - 1); length > 0; length--)
        {
            var tail = text[^length..];
            if (OpenTag.StartsWith(tail, StringComparison.OrdinalIgnoreCase) || CloseTag.StartsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                return length;
            }
        }

        return 0;
    }
}
