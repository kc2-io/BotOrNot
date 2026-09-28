using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Services;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;
using System.Reactive.Linq;

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

    [Test]
    public async Task IncompleteOwnerEvents_ShowObservedCoverageWithoutInventingHumanKills()
    {
        var replay = new ReplayData { OwnerName = "Owner", OwnerKills = 1 };
        replay.OwnerEliminations.Add(new PlayerRow { Name = "Bot", Bot = "true" });
        replay.OwnerEliminations.Add(new PlayerRow { Name = "Player", Bot = "false" });
        var viewModel = new MainWindowViewModel(
            replayService: new SequenceReplayService(replay),
            themeService: new ThemeService(new SettingsService(_settingsPath)));

        await viewModel.LoadReplayCommand.Execute("first").FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.EliminationCoverageNotice,
                Is.EqualTo("Replay records 1 eliminations; 2 credited events observed."));
            Assert.That(viewModel.OwnerKillsHeader, Does.Contain("1 Players observed, 1 Bots observed"));
        });
        viewModel.FilterText = "no-match";
        Assert.That(viewModel.EliminationCoverageNotice, Does.Contain("2 credited events observed"));
        await viewModel.LoadReplayCommand.Execute("reset-after-failure").FirstAsync();
        Assert.That(viewModel.EliminationCoverageNotice, Is.Null);
    }

    [Test]
    public async Task MissingCreditedEvents_ShowCoverageNotice()
    {
        var replay = new ReplayData { OwnerName = "Owner", OwnerKills = 3 };
        replay.OwnerEliminations.Add(new PlayerRow { Name = "One", Bot = "false" });
        replay.OwnerEliminations.Add(new PlayerRow { Name = "Two", Bot = "false" });
        var viewModel = new MainWindowViewModel(
            replayService: new SequenceReplayService(replay),
            themeService: new ThemeService(new SettingsService(_settingsPath)));

        await viewModel.LoadReplayCommand.Execute("first").FirstAsync();

        Assert.That(viewModel.EliminationCoverageNotice,
            Is.EqualTo("Replay records 3 eliminations; 2 credited events observed."));
        Assert.That(viewModel.OwnerKillsHeader, Does.Contain("2 Players observed, 0 Bots observed"));
    }

    [Test]
    public async Task UncertainAttribution_WithMatchingCountStillMarksObservedBreakdown()
    {
        var replay = new ReplayData { OwnerName = "Owner", OwnerKills = 2, HasUncertainEliminationAttribution = true };
        replay.OwnerEliminations.Add(new PlayerRow { Name = "Bot", Bot = "true" });
        replay.OwnerEliminations.Add(new PlayerRow { Name = "Player", Bot = "false" });
        var viewModel = new MainWindowViewModel(
            replayService: new SequenceReplayService(replay),
            themeService: new ThemeService(new SettingsService(_settingsPath)));

        await viewModel.LoadReplayCommand.Execute("uncertain").FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.EliminationCoverageNotice, Does.Contain("2 credited events observed").And.Contain("attribution is uncertain"));
            Assert.That(viewModel.OwnerKillsHeader, Does.Contain("Players observed").And.Contain("Bots observed"));
            Assert.That(viewModel.ElimsSummary, Is.EqualTo("2 Elims (1 Bot observed)"));
        });
    }

    [Test]
    public async Task UncertainAttribution_WithoutAuthoritativeTotalKeepsSummaryUnknown()
    {
        var replay = new ReplayData { OwnerName = "Owner", HasUncertainEliminationAttribution = true };
        replay.OwnerEliminations.Add(new PlayerRow { Name = "Player", Bot = "false" });
        var viewModel = new MainWindowViewModel(
            replayService: new SequenceReplayService(replay),
            themeService: new ThemeService(new SettingsService(_settingsPath)));

        await viewModel.LoadReplayCommand.Execute("uncertain-no-total").FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.ElimsSummary, Is.EqualTo("Eliminations unknown"));
            Assert.That(viewModel.OwnerKillsHeader, Does.Contain("elimination count is unknown"));
            Assert.That(viewModel.EliminationCoverageNotice, Does.Contain("attribution is uncertain").And.Contain("1 credited events observed"));
        });
    }

    private sealed class SequenceReplayService(params ReplayData[] replays) : IReplayService
    {
        private readonly Queue<ReplayData> _replays = new(replays);

        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(_replays.Dequeue());
    }
}
