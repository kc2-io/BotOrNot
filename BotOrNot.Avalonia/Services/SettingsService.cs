using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Styling;

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
}

public static class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotOrNot");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable file — fall back to defaults
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort save — don't crash the app
        }
    }

    public static ThemeVariant ToThemeVariant(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };
}
