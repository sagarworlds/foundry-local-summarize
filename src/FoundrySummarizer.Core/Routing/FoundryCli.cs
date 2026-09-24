using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FoundrySummarizer.Core.Routing;

/// <summary>Result of running a <c>foundry</c> CLI command.</summary>
/// <param name="Succeeded">True when the command ran and exited with code 0.</param>
/// <param name="Output">Combined standard output and error.</param>
/// <param name="Problem">Why the command could not run or failed; null on success.</param>
public record FoundryCliResult(bool Succeeded, string Output, string? Problem);

/// <summary>Runs Foundry Local CLI commands. Abstracted so discovery can be tested without the CLI installed.</summary>
public interface IFoundryCli
{
    /// <summary>Runs <c>foundry {arguments}</c>.</summary>
    /// <param name="arguments">E.g. "service status".</param>
    /// <param name="timeout">Kills the process after this long.</param>
    /// <param name="cancellationToken">Cancels and kills the process.</param>
    Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the real <c>foundry</c> executable. Foundry Local starts its service on a new port each time, and the
/// official SDK finds that port by running <c>foundry service status</c>; this class does the same.
/// </summary>
public sealed class FoundryCli : IFoundryCli
{
    // e.g. "🟢 Model management service is running on http://127.0.0.1:5273/openai/status"
    private static readonly Regex RunningOnUrl = new(@"running on\s+(?<url>https?://[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public async Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("foundry", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new FoundryCliResult(false, string.Empty,
                "The 'foundry' command was not found. Install Foundry Local (winget install Microsoft.FoundryLocal) or set Foundry:Local:Endpoint explicitly.");
        }

        using (process)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                var output = (await stdout) + (await stderr);
                return process.ExitCode == 0
                    ? new FoundryCliResult(true, output, null)
                    : new FoundryCliResult(false, output, $"'foundry {arguments}' exited with code {process.ExitCode}: {output.Trim()}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new FoundryCliResult(false, string.Empty, $"'foundry {arguments}' did not finish within {timeout.TotalSeconds:F0}s.");
            }
        }
    }

    /// <summary>
    /// Reads the service base address from <c>foundry service status</c> output.
    /// </summary>
    /// <returns>The scheme and authority (e.g. http://127.0.0.1:5273), or null when the output has no URL.</returns>
    public static Uri? ParseServiceUri(string statusOutput)
    {
        var match = RunningOnUrl.Match(statusOutput ?? string.Empty);
        return match.Success && Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var url)
            ? new Uri($"{url.Scheme}://{url.Authority}")
            : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill; nothing to clean up.
        }
    }
}
