using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Renames <c>max_completion_tokens</c> to <c>max_tokens</c> in outgoing chat requests.
/// The OpenAI .NET SDK (2.x) sends the output limit as <c>max_completion_tokens</c>, a field OpenAI introduced
/// in 2024. Foundry Local's OpenAI-compatible endpoint only accepts the original <c>max_tokens</c> and answers
/// HTTP 400 (invalid_request_error) otherwise; Ollama accepts both, so the rename is safe for every local server.
/// </summary>
public sealed class MaxTokensCompatibilityPolicy : PipelinePolicy
{
    private const string NewField = "max_completion_tokens";
    private const string LegacyField = "max_tokens";

    /// <inheritdoc />
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    /// <inheritdoc />
    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private static void Rewrite(PipelineMessage message)
    {
        if (message.Request.Content is not { } content) return;

        using var buffer = new MemoryStream();
        content.WriteTo(buffer);
        var rewritten = RewriteBody(buffer.ToArray());
        if (rewritten is not null)
        {
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(rewritten));
        }
    }

    /// <summary>Returns the body with the field renamed, or null when there is nothing to change.</summary>
    /// <param name="body">UTF-8 JSON request body.</param>
    public static byte[]? RewriteBody(byte[] body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // not JSON (e.g. a file upload); leave it untouched
        }

        if (root is not JsonObject json || !json.TryGetPropertyValue(NewField, out var value)) return null;

        json.Remove(NewField);
        if (!json.ContainsKey(LegacyField))
        {
            json[LegacyField] = value;
        }

        return System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
    }
}
