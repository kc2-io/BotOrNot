using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class PlaylistMappingTests
{
    [TestCase("Playlist_RopeSmileSolo", "Reload Build - Solo")]
    [TestCase("Playlist_RopeSmileDuo", "Reload Build - Duos")]
    [TestCase("Playlist_Habanero_JumpBear_Duos", "Ranked Reload Build - Duos")]
    [TestCase("Playlist_HabaneroDuo", "Ranked BR Build - Duos")]
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
}
