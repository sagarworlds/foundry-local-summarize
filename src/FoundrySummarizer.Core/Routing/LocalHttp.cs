using System.Net.Http;
using System.Text.Json;

namespace FoundrySummarizer.Core.Routing;

/// <summary>Result of one GET: the status and body, or why no response arrived.</summary>
/// <param name="Status">HTTP status code; null when no response arrived.</param>
/// <param name="Body">Response body (trimmed); empty when none.</param>
/// <param name="Error">Connection error or timeout; null when a response arrived.</param>
public record HttpGetResult(int? Status, string Body, string? Error)
{
    /// <summary>True for a 2xx response.</summary>
    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>One-line description for messages, e.g. "HTTP 400: Model not cached".</summary>
    public string Describe() =>
        Error ?? $"HTTP {Status}{(Body.Length > 0 ? ": " + (Body.Length <= 200 ? Body : Body[..200] + "…") : "")}";
}

/// <summary>Small GET helper shared by the model-management APIs; turns every failure into a result, never an exception.</summary>
public static class LocalHttp
{
    /// <summary>GETs <paramref name="path"/> on <paramref name="serviceBase"/>.</summary>
    /// <param name="http">Client with no timeout of its own; <paramref name="timeout"/> applies.</param>
    /// <param name="serviceBase">Scheme and authority of the server.</param>
    /// <param name="path">Absolute path and query, e.g. "/models/loaded".</param>
    /// <param name="timeout">Maximum wait.</param>
    /// <param name="cancellationToken">Caller cancellation; rethrown as <see cref="OperationCanceledException"/>.</param>
    public static async Task<HttpGetResult> GetAsync(HttpClient http, Uri serviceBase, string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var url = new Uri(serviceBase, path);
        try
        {
            using var response = await http.GetAsync(url, cts.Token);
            var body = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            return new HttpGetResult((int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HttpGetResult(null, string.Empty, $"{url} did not answer within {timeout.TotalSeconds:F0}s");
        }
        catch (HttpRequestException ex)
        {
            return new HttpGetResult(null, string.Empty, $"{url} failed: {ex.Message}");
        }
    }

    /// <summary>GETs <paramref name="path"/> and reads one string property of the JSON object it returns.</summary>
    /// <param name="http">Client with no timeout of its own; <paramref name="timeout"/> applies.</param>
    /// <param name="serviceBase">Scheme and authority of the server.</param>
    /// <param name="path">Absolute path, e.g. "/status".</param>
    /// <param name="property">Property name, matched case-insensitively (servers differ in casing).</param>
    /// <param name="timeout">Maximum wait.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>The value, or null when the request fails, the body is not a JSON object, or the property is missing.</returns>
    public static async Task<string?> GetJsonStringAsync(HttpClient http, Uri serviceBase, string path, string property, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await GetAsync(http, serviceBase, path, timeout, cancellationToken);
        if (!result.IsSuccess) return null;

        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var match = doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase));
            return match.Value.ValueKind == JsonValueKind.String ? match.Value.GetString() : null;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalHttp] {path} returned unreadable JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>Escapes a model name for a URL path, keeping ':' readable as Foundry Local expects.</summary>
    public static string PathSegment(string name) =>
        Uri.EscapeDataString(name).Replace("%3A", ":", StringComparison.OrdinalIgnoreCase);
}
