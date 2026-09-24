using System.Runtime.InteropServices;
using System.Windows;

namespace FoundrySummarizer.Wpf.Services;

/// <summary>Copies text to the system clipboard.</summary>
public interface IClipboardService
{
    /// <summary>Places <paramref name="text"/> on the clipboard.</summary>
    /// <returns>Null on success; otherwise why the copy failed.</returns>
    string? TrySetText(string text);
}

/// <summary>Uses the WPF clipboard.</summary>
public sealed class WpfClipboardService : IClipboardService
{
    /// <inheritdoc />
    public string? TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return null;
        }
        catch (COMException ex)
        {
            // Another application (e.g. a clipboard manager) can hold the clipboard open; retrying later works.
            return $"The clipboard is in use by another application ({ex.Message}). Try again.";
        }
    }
}
