using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotOrNot.Avalonia.Services;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public string? ReplayDirectory { get; set; }
    public int ReplayScanLimit { get; set; } = DefaultReplayScanLimit;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public const int DefaultReplayScanLimit = 50;
}

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    void Update(Action<AppSettings> update);
}

public sealed class SettingsService : ISettingsService
{
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly object _sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BotOrNot",
            "settings.json");
        _settingsDirectory = Path.GetDirectoryName(_settingsPath) ?? ".";
    }

    public AppSettings Load()
    {
        lock (_sync)
            return LoadCore();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
            SaveCore(settings);
    }

    public void Update(Action<AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_sync)
        {
            var settings = LoadCore();
            update(settings);
            SaveCore(settings);
        }
    }

    private AppSettings LoadCore()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return Normalize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings());
            }
        }
        catch
        {
            // Corrupt or unreadable file — fall back to defaults
        }

        return new AppSettings();
    }

    private void SaveCore(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Best-effort save — don't crash the app
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme))
            settings.Theme = ThemePreference.System;

        if (settings.ReplayScanLimit <= 0)
            settings.ReplayScanLimit = AppSettings.DefaultReplayScanLimit;

        return settings;
    }
}
