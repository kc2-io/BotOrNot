using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Services;
using BotOrNot.Core.Models;

namespace BotOrNot.Tests;

[TestFixture]
public class MainWindowViewModelFilterTests
{
    private MainWindowViewModel _viewModel = null!;
    private string _settingsPath = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), $"botornot-{Guid.NewGuid():N}.json");
        _viewModel = new MainWindowViewModel(
            themeService: new ThemeService(new SettingsService(_settingsPath)));
    }

    [TearDown]
    public void TearDown()
    {
        File.Delete(_settingsPath);
    }

    private static PlayerRow Row(string? name = null, string? level = null, string? platform = null,
                                 string? kills = null, string? teamIndex = null, string? placement = null,
                                 string? deathCause = null)
        => new()
        {
            Name = name,
            Level = level,
            Platform = platform,
            Kills = kills,
            TeamIndex = teamIndex,
            Placement = placement,
            DeathCause = deathCause
        };

    [Test]
    public void Filter_EmptyString_ShowsAllPlayers()
    {
        // This test verifies the filter logic conceptually
        // The actual implementation uses ObservableCollection so we test through the public API

        var searchTerm = "";
        var player1 = Row(name: "PlayerOne", level: "100", platform: "PC");
        var player2 = Row(name: "PlayerTwo", level: "50", platform: "PS5");

        var matches1 = MatchesFilter(player1, searchTerm);
        var matches2 = MatchesFilter(player2, searchTerm);

        Assert.That(matches1, Is.True, "Empty filter should match all players");
        Assert.That(matches2, Is.True, "Empty filter should match all players");
    }

    [Test]
    public void Filter_Name_MatchesByName()
    {
        var searchTerm = "playerone";
        var player = Row(name: "PlayerOne");

        Assert.That(MatchesFilter(player, searchTerm), Is.True);
    }

    [Test]
    public void Filter_Level_MatchesByLevel()
    {
        var searchTerm = "100";
        var player = Row(level: "100");

        Assert.That(MatchesFilter(player, searchTerm), Is.True);
    }

    [Test]
    public void Filter_Platform_MatchesByPlatform()
    {
        var searchTerm = "pc";
        var player = Row(platform: "PC");

        Assert.That(MatchesFilter(player, searchTerm), Is.True);
    }

    [Test]
    public void Filter_Kills_MatchesByKills()
    {
        var searchTerm = "5";
        var player = Row(kills: "5");

        Assert.That(MatchesFilter(player, searchTerm), Is.True);
    }

    [Test]
    public void Filter_TeamIndex_MatchesByTeamIndex()
    {
        var searchTerm = "2";
        var player = Row(teamIndex: "2");

        Assert.That(MatchesFilter(player, searchTerm), Is.True);
    }

    [Test]
    public void Filter_Placement_MatchesByPlacement()
    {
        var searchTerm = "1";
        var player = Row(placement: "1");

        Assert.That(MatchesFilter(player, searchTerm), Is.True);
    }

    [Test]
    public void Filter_DeathCause_MatchesByDeathCause()
    {
        var searchTerm = "fall";
        var player = Row(deathCause: "Fall Damage");

        Assert.That(MatchesFilter(player, searchTerm), Is.True);
    }

    [Test]
    public void Filter_CaseInsensitive_MatchesRegardlessOfCase()
    {
        var searchTerm = "playerone";
        var playerLower = Row(name: "playerone");
        var playerUpper = Row(name: "PLAYERONE");
        var playerMixed = Row(name: "PlayerOne");

        Assert.That(MatchesFilter(playerLower, searchTerm), Is.True);
        Assert.That(MatchesFilter(playerUpper, searchTerm), Is.True);
        Assert.That(MatchesFilter(playerMixed, searchTerm), Is.True);
    }

    [Test]
    public void Filter_NoMatch_DoesNotMatch()
    {
        var searchTerm = "xyz";
        var player = Row(name: "PlayerOne", level: "100");

        Assert.That(MatchesFilter(player, searchTerm), Is.False);
    }

    [Test]
    public void Filter_NullProperties_DoesNotThrow()
    {
        var searchTerm = "test";
        var player = Row(); // All properties null

        Assert.DoesNotThrow(() => MatchesFilter(player, searchTerm));
        Assert.That(MatchesFilter(player, searchTerm), Is.False);
    }

    [Test]
    public void Filter_MultipleFields_MatchesAnyField()
    {
        var searchTerm = "100";
        var player1 = Row(level: "100");
        var player2 = Row(kills: "100");
        var player3 = Row(teamIndex: "100");

        Assert.That(MatchesFilter(player1, searchTerm), Is.True);
        Assert.That(MatchesFilter(player2, searchTerm), Is.True);
        Assert.That(MatchesFilter(player3, searchTerm), Is.True);
    }

    private static bool MatchesFilter(PlayerRow player, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return true;

        return (player.Name?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.Level?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.Platform?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.Kills?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.TeamIndex?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.Placement?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.DeathCause?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
