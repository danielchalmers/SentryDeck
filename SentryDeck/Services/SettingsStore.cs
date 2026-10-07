using System.IO;
using System.Text.Json;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Keeps the user's choices across restarts in a small JSON file under %LOCALAPPDATA%\SentryDeck.
/// Reading and writing never throw, because a lost setting only means picking the folder again, while an exception here would stop the app from starting or the picked folder from opening.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <param name="path">The settings file.
    /// Defaults to settings.json next to the app's logs; tests pass their own so they never read or overwrite the user's settings.</param>
    public SettingsStore(string path = null)
    {
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SentryDeck",
            "settings.json");
    }

    public string FilePath { get; }

    /// <summary>
    /// The dashcam folders the user last picked, or none when they never picked one or the file can't be read.
    /// </summary>
    public IReadOnlyList<string> LoadPickedFolders() =>
        Load()?.PickedFolders?.Where(folder => !string.IsNullOrWhiteSpace(folder)).ToList() ?? [];

    public void SavePickedFolders(IEnumerable<string> folders)
    {
        // Read back first so saving this choice can't wipe out any other setting stored beside it.
        var settings = Load() ?? new SettingsFile();
        settings.PickedFolders = [.. folders];
        Save(settings);
    }

    private SettingsFile Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read settings. Path={Path}", FilePath);
            return null;
        }
    }

    private void Save(SettingsFile settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings. Path={Path}", FilePath);
        }
    }

    private sealed class SettingsFile
    {
        public List<string> PickedFolders { get; set; }
    }
}
