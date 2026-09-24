using System.Net.Http;

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

    /// <summary>Escapes a model name for a URL path, keeping ':' readable as Foundry Local expects.</summary>
    public static string PathSegment(string name) =>
        Uri.EscapeDataString(name).Replace("%3A", ":", StringComparison.OrdinalIgnoreCase);
}
