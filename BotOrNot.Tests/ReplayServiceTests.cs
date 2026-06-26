using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class PlaylistHelperTests
{
    [TestCase("Playlist_NoBuildBR_Solo", "BR Zero Build - Solo")]
    [TestCase("Playlist_NoBuildBR_Duo", "BR Zero Build - Duos")]
    [TestCase("Playlist_NoBuildBR_Trio", "BR Zero Build - Trios")]
    [TestCase("Playlist_NoBuildBR_Squad", "BR Zero Build - Squads")]
    [TestCase("Playlist_DefaultSolo", "BR Build - Solo")]
    [TestCase("Playlist_DefaultDuo", "BR Build - Duos")]
    [TestCase("Playlist_DefaultTrio", "BR Build - Trios")]
    [TestCase("Playlist_DefaultSquad", "BR Build - Squads")]
    public void GetDisplayName_ReturnsCorrectMapping(string playlist, string expectedDisplay)
    {
        var result = PlaylistHelper.GetDisplayName(playlist);
        Assert.That(result, Is.EqualTo(expectedDisplay));
    }

    [Test]
    public void GetDisplayName_IsCaseInsensitive()
    {
        var lower = PlaylistHelper.GetDisplayName("playlist_nobuildBR_solo");
        var upper = PlaylistHelper.GetDisplayName("PLAYLIST_NOBUILDGBR_SOLO");
        var mixed = PlaylistHelper.GetDisplayName("Playlist_NoBuildBR_Solo");

        Assert.That(lower, Is.EqualTo("BR Zero Build - Solo"));
        Assert.That(mixed, Is.EqualTo("BR Zero Build - Solo"));
    }

    [Test]
    public void GetDisplayName_ReturnsNullForUnknown()
    {
        var result = PlaylistHelper.GetDisplayName("Unknown_Playlist_Name");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetDisplayNameWithFallback_ReturnsRawNameForUnknown()
    {
        // Unknown playlist returns raw playlist string as fallback
        var result = PlaylistHelper.GetDisplayNameWithFallback("Playlist_NewMode_NoBuild_Squad");
        Assert.That(result, Is.EqualTo("Playlist_NewMode_NoBuild_Squad"));
    }
}

[TestFixture]
public class ReplayServiceTests
{
    private static string TestReplayPath => Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "TestData",
        "UnsavedReplay-2026.01.31-15.34.27.replay");

    /// <summary>
    /// Validates elimination counting logic for the recording player.
    /// An elimination counts when:
    /// 1. You knock someone and they get finished (by anyone) - they weren't revived
    /// 2. You directly eliminate a solo/last-standing player (who can't be knocked)
    /// </summary>
    [Test]
    public async Task RecordingPlayer_ShouldHave8Eliminations()
    {
        // Arrange
        var service = new ReplayService();

        // Act
        var result = await service.LoadReplayAsync(TestReplayPath);

        // Assert
        Assert.That(result.OwnerName, Is.Not.Null.And.Not.Empty,
            "Could not find replay owner");

        Assert.That(result.OwnerEliminations.Count, Is.EqualTo(8),
            $"Expected recording player ({result.OwnerName}) to have 8 eliminations, " +
            $"but found {result.OwnerEliminations.Count}");
    }

    [Test]
    public async Task EliminationsList_ShouldNotIncludeKnocks()
    {
        // Arrange
        var service = new ReplayService();

        // Act
        var result = await service.LoadReplayAsync(TestReplayPath);

        // Assert - elimination count should match the filtered list
        // The elimination count should only count finishes, not knocks
        Assert.That(result.Metadata.EliminationCount, Is.GreaterThan(0),
            "Should have some eliminations");
    }

    [Test]
    public async Task FancyPumpkin87_ShouldHaveDeathCauseOfSMG()
    {
        // Arrange
        var service = new ReplayService();

        // Act
        var result = await service.LoadReplayAsync(TestReplayPath);

        // Assert - find FancyPumpkin87 in owner eliminations and verify death cause
        var victim = result.OwnerEliminations.FirstOrDefault(p =>
            p.Name?.Equals("FancyPumpkin87", StringComparison.OrdinalIgnoreCase) == true);

        Assert.That(victim, Is.Not.Null,
            "FancyPumpkin87 should be in the owner's eliminations");

        Assert.That(victim!.DeathCause, Does.StartWith("SMG"),
            $"FancyPumpkin87's death cause should be SMG, but was {victim.DeathCause}");
    }

    [Test]
    public async Task WinningTeam_ShouldBeExtracted()
    {
        // Arrange
        var service = new ReplayService();

        // Act
        var result = await service.LoadReplayAsync(TestReplayPath);

        // Assert - winning team should be team 10
        Assert.That(result.Metadata.WinningTeam, Is.EqualTo(10),
            "Winning team should be team 10");

        Assert.That(result.Metadata.WinningPlayerNames, Has.Count.EqualTo(3),
            "Winning team should have 3 players");

        Assert.That(result.Metadata.WinningPlayerNames, Does.Contain("ModPackDad"),
            "ModPackDad should be one of the winning players");
    }

    [Test]
    public async Task WinningPlayers_ShouldHavePlacement1()
    {
        // Arrange
        var service = new ReplayService();

        // Act
        var result = await service.LoadReplayAsync(TestReplayPath);

        // Assert - find winning team players and verify placement
        var modPackDad = result.Players.FirstOrDefault(p =>
            p.Name?.Equals("ModPackDad", StringComparison.OrdinalIgnoreCase) == true);

        Assert.That(modPackDad, Is.Not.Null,
            "ModPackDad should be in the players list");

        Assert.That(modPackDad!.Placement, Is.EqualTo("1"),
            $"ModPackDad's placement should be 1, but was {modPackDad.Placement}");

        Assert.That(modPackDad.IsWinner, Is.True,
            "ModPackDad should be marked as winner");

        // All team 10 players should have placement 1
        var team10Players = result.Players.Where(p => p.TeamIndex == "10").ToList();
        Assert.That(team10Players.All(p => p.Placement == "1"), Is.True,
            "All team 10 players should have placement 1");
    }

    [Test]
    public async Task TeamKills_ShouldBeExtracted()
    {
        // Arrange
        var service = new ReplayService();

        // Act
        var result = await service.LoadReplayAsync(TestReplayPath);

        // Assert - team 10 should have 20 team kills
        var team10Player = result.Players.FirstOrDefault(p => p.TeamIndex == "10");

        Assert.That(team10Player, Is.Not.Null,
            "Should have players from team 10");

        Assert.That(team10Player!.TeamKills, Is.EqualTo("20"),
            $"Team 10 should have 20 team kills, but had {team10Player.TeamKills}");
    }

    /// <summary>
    /// Validate the total elim count we will display matches the known elim values provided for each file.
    /// </summary>
    /// <summary>
    /// After the Fortnite 6/25 update, Reload mode replays have empty GameData
    /// (RecorderId='', CurrentPlaylist='') and IsReplayOwner=False for all players.
    /// The owner must be detected via the IsSelfElimination fallback.
    /// </summary>
    [Test]
    public async Task Reload_PostJune2025Update_ShouldDetectOwner()
    {
        var service = new ReplayService();
        var replayPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "TestData",
            "Reload_BrokenGameData_June2025_Owner_Elim_1.replay");

        var result = await service.LoadReplayAsync(replayPath);

        Assert.That(result.OwnerName, Is.Not.Null.And.Not.Empty,
            "Owner should be detected via IsSelfElimination fallback when IsReplayOwner is missing");
        Assert.That(result.OwnerName, Is.EqualTo("ModPackDad"),
            "Owner should be ModPackDad");
    }

    [TestCase("Blitz_ForbiddenFruit_CalmSambucusBRSquad_Owner_Elim_1_Team_Elim_3_Place_3.replay", 1)]
    [TestCase("Blitz_ForbiddenFruitNoBuildBRSquad_Owner_Elim_1_Team_Elim_12_Place_1.replay", 1)]
    [TestCase("Reload_PunchBerryDuo_Owner_Elim_5_Team_Elim_1_Place_1.replay", 5)]
    [TestCase("Reload_BrokenGameData_June2025_Owner_Elim_1.replay", 1)]
    public async Task OwnerElim_ShouldShowCorrectElimCount(string replayFileName, int expectedElimCount)
    {
        // Arrange
        var service = new ReplayService();
        var replayPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "TestData",
            replayFileName);

        var result = await service.LoadReplayAsync(replayPath);

        var elimCount = result.OwnerKills;

        Assert.That(elimCount, Is.EqualTo(expectedElimCount),
            $"Expected owner elim to contain {expectedElimCount} for {replayFileName}, " +
            $"but got {elimCount} (OwnerKills={result.OwnerKills})");
    }

    /// <summary>
    /// Validate the total elim count we will display matches the known elim values provided for each file.
    /// </summary>
    [Test]
    public async Task Fortnite4100_Replay_ShouldLoadWithoutError()
    {
        var service = new ReplayService();
        var replayPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "TestData",
            "UnsavedReplay-2026.06.10-06.22.43.replay");

        var result = await service.LoadReplayAsync(replayPath);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Players, Is.Not.Empty,
            "Fortnite 41.00 replay should parse players — regression test for infinite loop and ShortComponents rotation fix");
    }

    [TestCase("Blitz_ForbiddenFruit_CalmSambucusBRSquad_Owner_Elim_1_Team_Elim_3_Place_3.replay", 1)]
    [TestCase("Blitz_ForbiddenFruitNoBuildBRSquad_Owner_Elim_1_Team_Elim_12_Place_1.replay", 1)]
    [TestCase("Reload_PunchBerryDuo_Owner_Elim_5_Team_Elim_1_Place_1.replay", 5)]
    [TestCase("Reload_BrokenGameData_June2025_Owner_Elim_1.replay", 1)]
    public async Task OwnerElimListLengthMatchesElimCount(string replayFileName, int expectedElimCount)
    {
        // Arrange
        var service = new ReplayService();
        var replayPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "TestData",
            replayFileName);

        var result = await service.LoadReplayAsync(replayPath);
        var elimCount = result.OwnerEliminations.Count;


        Assert.That(elimCount, Is.EqualTo(expectedElimCount),
            $"Expected owner elim to contain {expectedElimCount} for {replayFileName}, " +
            $"but got {elimCount} (OwnerKills={result.OwnerKills})");
    }
}

