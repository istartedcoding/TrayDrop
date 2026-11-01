using System.Text.Json;

namespace TrayDrop.Core;

public sealed class SaveLocationStore
{
    private sealed record StoredSettings(string? ReceiveDirectory);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SaveLocationStore(string dataDirectory, string defaultDirectory)
    {
        SettingsPath = Path.Combine(Path.GetFullPath(dataDirectory), "settings.json");
        DefaultDirectory = NormalizeDirectory(defaultDirectory);
        CurrentDirectory = DefaultDirectory;
        IsDefault = true;
        if (!File.Exists(SettingsPath))
            return;

        try
        {
            var settings = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                           ?? throw new InvalidDataException("TrayDrop's settings file is empty.");
            if (settings.ReceiveDirectory is not null)
            {
                CurrentDirectory = NormalizeDirectory(settings.ReceiveDirectory);
                IsDefault = false;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "TrayDrop's settings file is invalid. Correct or remove " + SettingsPath + " and restart the app.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "TrayDrop's saved folder is invalid. Correct or remove " + SettingsPath + " and restart the app.",
                exception);
        }
    }

    public string SettingsPath { get; }
    public string DefaultDirectory { get; }
    public string CurrentDirectory { get; private set; }
    public bool IsDefault { get; private set; }

    public void Save(string? customDirectory)
    {
        var directory = customDirectory is null ? DefaultDirectory : NormalizeDirectory(customDirectory);
        var settings = new StoredSettings(customDirectory is null ? null : directory);
        var parent = Path.GetDirectoryName(SettingsPath)
                     ?? throw new InvalidOperationException("The settings directory could not be determined.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
            CurrentDirectory = directory;
            IsDefault = customDirectory is null;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Choose an absolute folder path for received files.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }
}
