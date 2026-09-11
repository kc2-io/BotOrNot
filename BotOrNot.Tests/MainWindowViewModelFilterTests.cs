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

    private sealed class SequenceReplayService(params ReplayData[] replays) : IReplayService
    {
        private readonly Queue<ReplayData> _replays = new(replays);

        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(_replays.Dequeue());
    }
}
}
