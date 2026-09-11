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
            Playlist = "Playlist_NotInMappings", GameMode = "Outdated label"
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
