namespace FoundrySummarizer.Wpf.Services;

/// <summary>Asks the user to choose a document file.</summary>
public interface IDocumentPicker
{
    /// <summary>Shows the picker.</summary>
    /// <param name="supportedExtensions">Extensions (with leading dot) the app can read.</param>
    /// <returns>The chosen file's full path, or null if the user cancelled.</returns>
    string? PickDocument(IReadOnlyCollection<string> supportedExtensions);
}
