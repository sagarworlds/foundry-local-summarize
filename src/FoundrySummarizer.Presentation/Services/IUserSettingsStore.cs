using System.IO;
using System.Text.Json;

namespace FoundrySummarizer.Presentation.Services;

/// <summary>Choices the user makes in the app that should survive a restart.</summary>
public record UserSettings
{
    /// <summary>The model picked in the header, or null to choose automatically.</summary>
    public string? SelectedModelId { get; init; }
}

/// <summary>Loads and saves <see cref="UserSettings"/>.</summary>
public interface IUserSettingsStore
{
    /// <summary>Returns the saved settings, or defaults when none are saved or the file is unreadable.</summary>
    UserSettings Load();

    /// <summary>Saves <paramref name="settings"/>.</summary>
    /// <returns>Null on success; otherwise why saving failed.</returns>
    string? Save(UserSettings settings);
}

/// <summary>Keeps user settings in %LOCALAPPDATA%\FoundrySummarizer\user-settings.json (appsettings.json stays read-only).</summary>
public sealed class JsonUserSettingsStore : IUserSettingsStore
{
    private readonly string _path;

    /// <param name="path">Settings file; defaults to %LOCALAPPDATA%\FoundrySummarizer\user-settings.json.</param>
    public JsonUserSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoundrySummarizer", "user-settings.json");
    }

    /// <inheritdoc />
    public UserSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path)) ?? new UserSettings()
                : new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged settings file only loses the remembered model; the app still works with automatic selection.
            System.Diagnostics.Debug.WriteLine($"[JsonUserSettingsStore] Ignoring unreadable {_path}: {ex.Message}");
            return new UserSettings();
        }
    }

    /// <inheritdoc />
    public string? Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not remember the model choice ({ex.Message}).";
        }
    }
}
