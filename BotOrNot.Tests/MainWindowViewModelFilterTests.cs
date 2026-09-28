using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Core.Models;

namespace BotOrNot.Tests;

[TestFixture]
public class MainWindowViewModelFilterTests
{
    private static PlayerRow Row(string? name = null, string? level = null, string? platform = null,
                                 string? kills = null, string? teamIndex = null, string? placement = null,
                                 string? deathCause = null, string? elimTime = null)
        => new()
        {
            Name = name,
            Level = level,
            Platform = platform,
            Kills = kills,
            TeamIndex = teamIndex,
            Placement = placement,
            DeathCause = deathCause,
            ElimTime = elimTime
        };

    [Test]
    public void Filter_EmptyString_ShowsAllPlayers()
    {
        var player1 = Row(name: "PlayerOne", level: "100", platform: "PC");
        var player2 = Row(name: "PlayerTwo", level: "50", platform: "PS5");

        Assert.That(MainWindowViewModel.MatchesFilter(player1, ""), Is.True);
        Assert.That(MainWindowViewModel.MatchesFilter(player2, ""), Is.True);
    }

    [Test]
    public void Filter_Name_MatchesByName()
    {
        var player = Row(name: "PlayerOne");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "playerone"), Is.True);
    }

    [Test]
    public void Filter_Level_MatchesByLevel()
    {
        var player = Row(level: "100");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "100"), Is.True);
    }

    [Test]
    public void Filter_Platform_MatchesByFriendlyName()
    {
        var player = Row(platform: "WIN");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "pc"), Is.True);
    }

    [Test]
    public void Filter_Kills_MatchesByKills()
    {
        var player = Row(kills: "5");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "5"), Is.True);
    }

    [Test]
    public void Filter_TeamIndex_MatchesByTeamIndex()
    {
        var player = Row(teamIndex: "2");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "2"), Is.True);
    }

    [Test]
    public void Filter_Placement_MatchesByPlacement()
    {
        var player = Row(placement: "1");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "1"), Is.True);
    }

    [Test]
    public void Filter_DeathCause_MatchesByDeathCause()
    {
        var player = Row(deathCause: "Fall Damage");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "fall"), Is.True);
    }

    [Test]
    public void Filter_ElimTime_MatchesByElimTime()
    {
        var player = Row(elimTime: "00:01:23");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "01:23"), Is.True);
    }

    [Test]
    public void Filter_CaseInsensitive_MatchesRegardlessOfCase()
    {
        var playerLower = Row(name: "playerone");
        var playerUpper = Row(name: "PLAYERONE");
        var playerMixed = Row(name: "PlayerOne");

        Assert.That(MainWindowViewModel.MatchesFilter(playerLower, "playerone"), Is.True);
        Assert.That(MainWindowViewModel.MatchesFilter(playerUpper, "playerone"), Is.True);
        Assert.That(MainWindowViewModel.MatchesFilter(playerMixed, "playerone"), Is.True);
    }

    [Test]
    public void Filter_NoMatch_DoesNotMatch()
    {
        var player = Row(name: "PlayerOne", level: "100");
        Assert.That(MainWindowViewModel.MatchesFilter(player, "xyz"), Is.False);
    }

    [Test]
    public void Filter_NullProperties_DoesNotThrow()
    {
        var player = Row();

        Assert.DoesNotThrow(() => MainWindowViewModel.MatchesFilter(player, "test"));
        Assert.That(MainWindowViewModel.MatchesFilter(player, "test"), Is.False);
    }

    [Test]
    public void Filter_MultipleFields_MatchesAnyField()
    {
        var player1 = Row(level: "100");
        var player2 = Row(kills: "100");
        var player3 = Row(teamIndex: "100");

        Assert.That(MainWindowViewModel.MatchesFilter(player1, "100"), Is.True);
        Assert.That(MainWindowViewModel.MatchesFilter(player2, "100"), Is.True);
        Assert.That(MainWindowViewModel.MatchesFilter(player3, "100"), Is.True);
    }

    [Test]
    public void NpcFilter_MatchesOnlyVisibleFields()
    {
        var npc = Row(name: "Wolf", level: "100", platform: "PC", kills: "0", teamIndex: "3", placement: "5", deathCause: "Rifle", elimTime: "00:02:00");

        Assert.That(MainWindowViewModel.MatchesNpcFilter(npc, "wolf"), Is.True);
        Assert.That(MainWindowViewModel.MatchesNpcFilter(npc, "Rifle"), Is.True);
        Assert.That(MainWindowViewModel.MatchesNpcFilter(npc, "100"), Is.False, "NPC filter should not match hidden Level");
        Assert.That(MainWindowViewModel.MatchesNpcFilter(npc, "PC"), Is.False, "NPC filter should not match hidden Platform");
        Assert.That(MainWindowViewModel.MatchesNpcFilter(npc, "0"), Is.False, "NPC filter should not match hidden Kills");
        Assert.That(MainWindowViewModel.MatchesNpcFilter(npc, "02:00"), Is.False, "NPC filter should not match hidden Elim Time");
    }
}
