using BotOrNot.Core.Services;
using System.Reflection;
using System.Text.Json;

namespace BotOrNot.Tests;

[TestFixture]
public class PlaylistMappingTests
{
    [TestCase("{\"GameMode\":\"Old label\",\"Playlist\":\"Playlist_MatchMistDuo\"}")]
    [TestCase("{\"Playlist\":\"Playlist_MatchMistDuo\",\"GameMode\":\"Old label\"}")]
    public void CachedSummaryUsesCurrentMappingRegardlessOfJsonPropertyOrder(string json)
    {
        var summary = JsonSerializer.Deserialize<BotOrNot.Core.Models.ReplaySummary>(json)!;
        Assert.That(summary.GameMode, Is.EqualTo("Reload Build - Duos"));
        var restored = JsonSerializer.Deserialize<BotOrNot.Core.Models.ReplaySummary>(
            JsonSerializer.Serialize(summary))!;
        Assert.That(restored.GameMode, Is.EqualTo("Reload Build - Duos"));
    }

    [Test]
    public void CachedSummaryPreservesUnknownRawIdAndLegacyLabelWithoutAnId()
    {
        var unknown = new BotOrNot.Core.Models.ReplaySummary
        {
            Playlist = "Playlist_NotInMappings",
            GameMode = "Outdated label"
        };
        Assert.That(unknown.GameMode, Is.EqualTo("Playlist_NotInMappings"));
        Assert.That(new BotOrNot.Core.Models.ReplaySummary { GameMode = "Legacy" }.GameMode,
            Is.EqualTo("Legacy"));
    }

    [TestCase("Playlist_RopeSmileSolo", "Reload Build - Solo")]
    [TestCase("Playlist_RopeSmileDuo", "Reload Build - Duos")]
    [TestCase("Playlist_Habanero_JumpBear_Duos", "Ranked Reload Build - Duos")]
    [TestCase("Playlist_HabaneroDuo", "Ranked BR Build - Duos")]
    [TestCase("Playlist_ForbiddenFruitOldNoBuildBRSolo", "Blitz Zero Build - Solo")]
    [TestCase("Playlist_MatchMistSolo", "Reload Build - Solo")]
    [TestCase("Playlist_MatchMistDuo", "Reload Build - Duos")]
    [TestCase("Playlist_MatchMistSquad", "Reload Build - Squads")]
    [TestCase("Playlist_NoBuildBR_Habanero_Solo", "Ranked Zero Build - Solo")]
    [TestCase("Playlist_Trios", "BR Build - Trios")]
    public void NewlyObservedPlaylistsHaveFamilyAndTeamNames(string playlist, string expectedDisplayName)
    {
        Assert.That(PlaylistHelper.GetDisplayName(playlist), Is.EqualTo(expectedDisplayName));
    }

    [Test]
    public void NewlyObservedPlaylistsAreCaseInsensitive()
    {
        Assert.That(
            PlaylistHelper.GetDisplayName("playlist_ropesmileduo"),
            Is.EqualTo("Reload Build - Duos"));
        Assert.That(
            PlaylistHelper.GetDisplayName("PLAYLIST_HABANERO_JUMPBEAR_DUOS"),
            Is.EqualTo("Ranked Reload Build - Duos"));
    }

    [Test]
    public void UnknownPlaylistKeepsRawFallback()
    {
        const string unknownPlaylist = "Playlist_NotInMappings";

        Assert.That(PlaylistHelper.GetDisplayName(unknownPlaylist), Is.Null);
        Assert.That(
            PlaylistHelper.GetDisplayNameWithFallback(unknownPlaylist),
            Is.EqualTo(unknownPlaylist));
    }

    [TestCase("Playlist_DefaultSolo", 1)]
    [TestCase("Playlist_MatchMistDuo", 2)]
    [TestCase("Playlist_PiperBootDuo", 2)]
    [TestCase("Playlist_PiperBootSquad", 4)]
    [TestCase("Playlist_Trios", 3)]
    [TestCase("Playlist_DefaultSquad", 4)]
    [TestCase("Playlist_DefaultTrio", 3)]
    [TestCase("playlist_defaultduo", 2)]
    [TestCase("Playlist_UncataloguedSolo", null)]
    public void KnownMaxTeamSizeRequiresExactCatalogEntry(string playlist, int? expectedTeamSize)
    {
        Assert.That(PlaylistHelper.GetKnownMaxTeamSize(playlist), Is.EqualTo(expectedTeamSize));
    }

    [Test]
    public void ExplicitCatalogTeamSizeTakesPrecedenceAndSupportsSixStack()
    {
        var catalog = PlaylistCatalog.FromJson("""
            { "playlists": [
              { "playlist_name": "Playlist_TestSixStack", "display_name": "Non-canonical label", "teamSize": 6 },
              { "playlist_name": "Playlist_LegacyTrio", "display_name": "Legacy mode - Trios" }
            ] }
            """);

        Assert.That(PlaylistHelper.GetKnownMaxTeamSize(catalog, "Playlist_TestSixStack"), Is.EqualTo(6));
        Assert.That(PlaylistHelper.GetKnownMaxTeamSize(catalog, "Playlist_LegacyTrio"), Is.EqualTo(3));
        Assert.That(PlaylistHelper.GetKnownMaxTeamSize(catalog, "Playlist_UncataloguedSixStack"), Is.Null);
    }

    [TestCase(6)]
    [TestCase(5)]
    public void ExplicitTeamSizeOverridesConflictingLegacyLabel(int teamSize)
    {
        var catalog = PlaylistCatalog.FromJson($$"""
            { "playlists": [
              { "playlist_name": "Playlist_Test", "display_name": "Legacy mode - Solo", "teamSize": {{teamSize}} }
            ] }
            """);

        Assert.That(PlaylistHelper.GetKnownMaxTeamSize(catalog, "Playlist_Test"), Is.EqualTo(teamSize));
    }

    [TestCase("Legacy mode - Solo", 1)]
    [TestCase("Legacy mode - Duo", 2)]
    [TestCase("Legacy mode - Duos", 2)]
    [TestCase("Legacy mode - Trio", 3)]
    [TestCase("Legacy mode - Trios", 3)]
    [TestCase("Legacy mode - Squad", 4)]
    [TestCase("Legacy mode - Squads", 4)]
    [TestCase("Legacy mode - Six-stack", 6)]
    [TestCase("Legacy mode - sQuAdS", 4)]
    [TestCase("Legacy mode - Six-STACK", 6)]
    [TestCase("Non-canonical label", null)]
    [TestCase("Legacy mode - Solo ", null)]
    [TestCase("Legacy mode - Solo extra", null)]
    [TestCase("Legacy mode Solo", null)]
    public void LegacyTeamSizeRequiresSupportedDisplayNameSuffix(string displayName, int? expectedTeamSize)
    {
        var catalog = PlaylistCatalog.FromJson($$"""
            { "playlists": [
              { "playlist_name": "Playlist_Test", "display_name": "{{displayName}}" }
            ] }
            """);

        Assert.That(PlaylistHelper.GetKnownMaxTeamSize(catalog, "playlist_test"), Is.EqualTo(expectedTeamSize));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Playlist_UncataloguedSolo")]
    [TestCase("Unknown mode - Solo")]
    public void TeamSizeRequiresKnownPlaylistId(string? playlistName)
    {
        var catalog = PlaylistCatalog.FromJson("""
            { "playlists": [
              { "playlist_name": "Playlist_Test", "display_name": "Legacy mode - Solo" }
            ] }
            """);

        Assert.That(PlaylistHelper.GetKnownMaxTeamSize(catalog, playlistName), Is.Null);
    }

    [TestCase("Playlist_ForbiddenFruitOldNoBuildBRSolo", "Blitz Zero Build - Solo")]
    [TestCase("Playlist_MatchMistSolo", "Reload Build - Solo")]
    [TestCase("Playlist_MatchMistDuo", "Reload Build - Duos")]
    [TestCase("Playlist_MatchMistSquad", "Reload Build - Squads")]
    [TestCase("Playlist_NoBuildBR_Habanero_Solo", "Ranked Zero Build - Solo")]
    [TestCase("Playlist_Trios", "BR Build - Trios")]
    public void Issue53PlaylistsAreCaseInsensitive(string playlist, string expectedDisplayName)
    {
        Assert.That(PlaylistHelper.GetDisplayName(playlist.ToLowerInvariant()), Is.EqualTo(expectedDisplayName));
        Assert.That(PlaylistHelper.GetDisplayName(playlist.ToUpperInvariant()), Is.EqualTo(expectedDisplayName));
    }

    [Test]
    public void PlaylistMappingKeysAreUniqueIgnoringCase()
    {
        var assembly = typeof(PlaylistHelper).Assembly;
        using var stream = assembly.GetManifestResourceStream("BotOrNot.Core.Data.PlaylistMappings.json");
        Assert.That(stream, Is.Not.Null);
        using var document = JsonDocument.Parse(stream!);
        var names = document.RootElement.GetProperty("playlists").EnumerateArray()
            .Select(item => item.GetProperty("playlist_name").GetString()!.Trim().ToUpperInvariant())
            .ToList();

        Assert.That(names, Is.EqualTo(names.Distinct().ToList()));
    }
}
