using System.Reflection;
using System.Text.Json;

namespace BotOrNot.Core.Services;

/// <summary>
/// Provides playlist name to display name mapping using data from Fortnite's content API.
/// Data source: https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/
/// </summary>
public static class PlaylistHelper
{
    private static readonly PlaylistCatalog Catalog;

    static PlaylistHelper()
    {
        Catalog = LoadPlaylistMappings();
    }

    private static PlaylistCatalog LoadPlaylistMappings()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "BotOrNot.Core.Data.PlaylistMappings.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return PlaylistCatalog.Empty;

            using var reader = new StreamReader(stream);
            return PlaylistCatalog.FromJson(reader.ReadToEnd());
        }
        catch
        {
            // If loading fails, return empty mappings - fallback logic will handle it
            return PlaylistCatalog.Empty;
        }
    }

    /// <summary>
    /// Gets the display name for a playlist. Returns null if no mapping exists.
    /// </summary>
    public static string? GetDisplayName(string? playlistName)
    {
        if (string.IsNullOrEmpty(playlistName))
            return null;

        return Catalog.GetDisplayName(playlistName);
    }

    /// <summary>
    /// Returns the maintained playlist catalog's team format. Structured <c>teamSize</c>
    /// descriptors take precedence; older catalog entries retain the canonical-label suffix
    /// fallback. Unknown raw IDs remain unknown even when their text contains a size word.
    /// </summary>
    public static int? GetKnownMaxTeamSize(string? playlistName)
        => GetKnownMaxTeamSize(Catalog, playlistName);

    internal static int? GetKnownMaxTeamSize(PlaylistCatalog catalog, string? playlistName)
    {
        if (string.IsNullOrEmpty(playlistName))
            return null;

        if (catalog.GetTeamSize(playlistName) is { } teamSize)
            return teamSize;

        var displayName = catalog.GetDisplayName(playlistName);
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
        if (displayName.EndsWith(" - Six-stack", StringComparison.OrdinalIgnoreCase))
            return 6;

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

internal sealed class PlaylistCatalog
{
    private readonly Dictionary<string, string> _displayNames;
    private readonly Dictionary<string, int> _teamSizes;

    private PlaylistCatalog(Dictionary<string, string> displayNames, Dictionary<string, int> teamSizes)
    {
        _displayNames = displayNames;
        _teamSizes = teamSizes;
    }

    internal static PlaylistCatalog Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    internal static PlaylistCatalog FromJson(string json)
    {
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var teamSizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("playlists", out var playlists) ||
            playlists.ValueKind != JsonValueKind.Array)
        {
            return new PlaylistCatalog(displayNames, teamSizes);
        }

        foreach (var playlist in playlists.EnumerateArray())
        {
            if (!playlist.TryGetProperty("playlist_name", out var nameElement) ||
                !playlist.TryGetProperty("display_name", out var displayElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            var display = displayElement.GetString();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(display))
                continue;

            displayNames[name] = display;
            if (playlist.TryGetProperty("teamSize", out var teamSizeElement) &&
                teamSizeElement.TryGetInt32(out var teamSize) && teamSize > 0)
            {
                teamSizes[name] = teamSize;
            }
        }

        return new PlaylistCatalog(displayNames, teamSizes);
    }

    internal string? GetDisplayName(string playlistName)
        => _displayNames.TryGetValue(playlistName, out var displayName) ? displayName : null;

    internal int? GetTeamSize(string playlistName)
        => _teamSizes.TryGetValue(playlistName, out var teamSize) ? teamSize : null;
}
