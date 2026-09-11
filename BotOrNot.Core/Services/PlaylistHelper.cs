using System.Reflection;
using System.Text.Json;

namespace BotOrNot.Core.Services;

/// <summary>
/// Provides playlist name to display name mapping using data from Fortnite's content API.
/// Data source: https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/
/// </summary>
public static class PlaylistHelper
{
    private static readonly Dictionary<string, string> PlaylistMappings;

    static PlaylistHelper()
    {
        PlaylistMappings = LoadPlaylistMappings();
    }

    private static Dictionary<string, string> LoadPlaylistMappings()
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "BotOrNot.Core.Data.PlaylistMappings.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return mappings;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("playlists", out var playlists))
            {
                foreach (var playlist in playlists.EnumerateArray())
                {
                    if (playlist.TryGetProperty("playlist_name", out var nameElement) &&
                        playlist.TryGetProperty("display_name", out var displayElement))
                    {
                        var name = nameElement.GetString();
                        var display = displayElement.GetString();

                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(display))
                        {
                            mappings[name] = display;
                        }
                    }
                }
            }
        }
        catch
        {
            // If loading fails, return empty mappings - fallback logic will handle it
        }

        return mappings;
    }

    /// <summary>
    /// Gets the display name for a playlist. Returns null if no mapping exists.
    /// </summary>
    public static string? GetDisplayName(string? playlistName)
    {
        if (string.IsNullOrEmpty(playlistName))
            return null;

        return PlaylistMappings.TryGetValue(playlistName, out var displayName) ? displayName : null;
    }

    /// <summary>
    /// Returns the maintained playlist catalog's team format from its canonical display-label
    /// suffix. This is an interim adapter until the catalog stores a structured max-team-size
    /// descriptor. Unknown raw IDs remain unknown even when their text contains a size word.
    /// </summary>
    public static int? GetKnownMaxTeamSize(string? playlistName)
    {
        var displayName = GetDisplayName(playlistName);
        if (displayName == null)
            return null;

        if (displayName.EndsWith(" - Solo", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (displayName.EndsWith(" - Duo", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith(" - Duos", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (displayName.EndsWith(" - Trio", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith(" - Trios", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (displayName.EndsWith(" - Squad", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith(" - Squads", StringComparison.OrdinalIgnoreCase))
            return 4;

        return null;
    }

    /// <summary>
    /// Gets the display name for a playlist, or returns the raw playlist string if no mapping exists.
    /// </summary>
    public static string GetDisplayNameWithFallback(string? playlistName)
    {
        // Try exact match first
        var displayName = GetDisplayName(playlistName);
        if (displayName != null)
            return displayName;

        // Return raw playlist name if no mapping found
        return playlistName ?? "Unknown";
    }
}
