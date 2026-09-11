using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;
using BotOrNot.UITests.Helpers;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class SquadSectionTests
{
    private string _settingsPath = null!;

    [SetUp]
    public void SetUp() => _settingsPath = Path.Combine(Path.GetTempPath(), $"botornot-squad-{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() => File.Delete(_settingsPath);

    [AvaloniaTest]
    public async Task TeamSection_IsBelowOwnerEliminations_AndIgnoresPlayerFilter()
    {
        var viewModel = CreateViewModel(TeamReplay(), SoloReplay());
        await viewModel.LoadReplayCommand.Execute("team").FirstAsync();
        var view = new MatchView { DataContext = viewModel };
        var window = new Window { Content = view };
        window.Show();

        var ownerGrid = view.FindControl<DataGrid>("OwnerEliminationsGrid");
        var squadGrid = view.FindControl<DataGrid>("SquadGrid");
        Assert.That(ownerGrid, Is.Not.Null);
        Assert.That(squadGrid, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.HasSquadSection, Is.True);
            Assert.That(viewModel.Teammates.Select(member => member.Name), Is.EqualTo(new[] { "Teammate" }));
            Assert.That(squadGrid!.IsVisible, Is.True);
            Assert.That(squadGrid.Bounds.Y, Is.GreaterThan(ownerGrid!.Bounds.Y));
            Assert.That(viewModel.SquadStatusText, Does.Contain("team eliminations 3"));
        });

        viewModel.FilterText = "does-not-match";
        Assert.That(viewModel.Teammates, Has.Count.EqualTo(1), "Opponent filters must not hide squad members.");
        window.Close();
    }

    [AvaloniaTest]
    public async Task LoadingConfirmedSoloAfterTeam_ClearsAndHidesPreviousSquad()
    {
        var viewModel = CreateViewModel(TeamReplay(), SoloReplay());
        await viewModel.LoadReplayCommand.Execute("team").FirstAsync();
        await viewModel.LoadReplayCommand.Execute("solo").FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.HasSquadSection, Is.False);
            Assert.That(viewModel.Teammates, Is.Empty);
            Assert.That(viewModel.SquadStatusText, Is.Null);
        });
    }

    [AvaloniaTest]
    public async Task SquadNumericColumns_SortNullableValuesNumerically()
    {
        var viewModel = CreateViewModel(TeamReplay());
        await viewModel.LoadReplayCommand.Execute("team").FirstAsync();
        viewModel.Teammates.Add(new SquadMemberSummary { StableId = "two", Name = "Two", Kills = 2 });
        viewModel.Teammates.Add(new SquadMemberSummary { StableId = "ten", Name = "Ten", Kills = 10 });
        viewModel.Teammates.Add(new SquadMemberSummary { StableId = "unknown", Name = "Unknown" });
        var view = new MatchView { DataContext = viewModel };
        var window = new Window { Content = view };
        window.Show();

        var grid = view.FindControl<DataGrid>("SquadGrid");
        Assert.That(grid, Is.Not.Null);
        DataGridTestHelper.ClickColumnHeader(window, grid!, "Kills");

        Assert.That(
            grid!.CollectionView!.Cast<SquadMemberSummary>().Select(member => member.Kills),
            Is.EqualTo(new int?[] { 0, 2, 10, null }));
        window.Close();
    }

    private MainWindowViewModel CreateViewModel(params ReplayData[] replays) => new(
        replayService: new SequenceReplayService(replays),
        themeService: new ThemeService(new SettingsService(_settingsPath)));

    private static ReplayData TeamReplay() => new()
    {
        OwnerId = "owner-id",
        OwnerTeamIndex = 1,
        OwnerName = "Owner",
        OwnerKills = 0,
        Metadata = new ReplayMetadata { FileName = "team.replay", Playlist = "Playlist_DefaultDuo" },
        Players =
        [
            Player("owner-id", "Owner", owner: true, teamKills: "3"),
            Player("teammate-id", "Teammate", teamKills: "3")
        ]
    };

    private static ReplayData SoloReplay() => new()
    {
        OwnerId = "solo-id",
        OwnerTeamIndex = 1,
        OwnerName = "Solo",
        OwnerKills = 0,
        Metadata = new ReplayMetadata { FileName = "solo.replay", Playlist = "Playlist_DefaultSolo" },
        Players = [Player("solo-id", "Solo", owner: true, teamKills: "0")]
    };

    private static PlayerRow Player(string id, string name, bool owner = false, string? teamKills = null) => new()
    {
        StableId = id,
        Id = id,
        Name = name,
        IsReplayOwner = owner,
        TeamIndexValue = 1,
        TeamIndex = "1",
        TeamKills = teamKills,
        Kills = "0"
    };

    private sealed class SequenceReplayService(params ReplayData[] replays) : IReplayService
    {
        private int _next;

        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(replays[_next++]);
    }
}
