using System.Runtime.InteropServices;
using System.Windows;
using FoundrySummarizer.Presentation.Services;

namespace FoundrySummarizer.Wpf.Services;

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
