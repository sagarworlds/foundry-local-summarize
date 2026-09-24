namespace FoundrySummarizer.Presentation.Services;

/// <summary>Copies text to the system clipboard.</summary>
public interface IClipboardService
{
    /// <summary>Places <paramref name="text"/> on the clipboard.</summary>
    /// <returns>Null on success; otherwise why the copy failed.</returns>
    string? TrySetText(string text);
}
