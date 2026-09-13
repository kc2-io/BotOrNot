using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        var view = new MatchView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        window.Show();
        await viewModel.LoadReplayCommand.Execute("team").FirstAsync();
        await viewModel.LoadReplayCommand.Execute("solo").FirstAsync();
        window.UpdateLayout();
        var squadGrid = view.FindControl<DataGrid>("SquadGrid")!;
        var layout = (Grid)squadGrid.Parent!;

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.HasSquadSection, Is.False);
            Assert.That(viewModel.Teammates, Is.Empty);
            Assert.That(viewModel.SquadStatusText, Is.Null);
            Assert.That(layout.RowDefinitions[Grid.GetRow(squadGrid)].ActualHeight, Is.Zero,
                "A hidden squad must not leave an empty star-sized region in solo replays.");
        });
        window.Close();
    }

    [AvaloniaTest]
    public async Task PartialUnknownAndFailedReplaysDoNotRetainPreviousSquadStats()
    {
        var partial = TeamReplay();
        partial.Players.RemoveAt(1);
        var unavailable = new ReplayData { Metadata = new ReplayMetadata { Playlist = "Playlist_DefaultDuo" } };
        var viewModel = CreateViewModel(TeamReplay(), partial, unavailable);
        await viewModel.LoadReplayCommand.Execute("team").FirstAsync();
        await viewModel.LoadReplayCommand.Execute("partial").FirstAsync();
        Assert.That(viewModel.HasSquadSection, Is.True);
        Assert.That(viewModel.Teammates, Is.Empty);
        Assert.That(viewModel.SquadStatusText, Does.Contain("partially observed"));
        await viewModel.LoadReplayCommand.Execute("unavailable").FirstAsync();
        Assert.That(viewModel.SquadStatusText, Does.Contain("unavailable").And.Contain("unknown"));
        await viewModel.LoadReplayCommand.Execute("failure").FirstAsync();
        Assert.That(viewModel.ErrorMessage, Does.Contain("sample failure"));
        Assert.That(viewModel.HasSquadSection, Is.False);
        Assert.That(viewModel.Teammates, Is.Empty);
        Assert.That(viewModel.SquadStatusText, Is.Null);
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

    [TestCase(1000)]
    [TestCase(1400)]
    [AvaloniaTest]
    public async Task SquadTable_RemovesBotColumn_AlignsWithOwnerGrid_AndLinksHumanNames(double windowWidth)
    {
        var viewModel = CreateViewModel(TeamReplay());
        await viewModel.LoadReplayCommand.Execute("team").FirstAsync();
        var view = new MatchView { DataContext = viewModel };
        var window = new Window { Content = view, Width = windowWidth, Height = 800 };
        window.Show();
        Render(window);

        viewModel.Teammates.Add(new SquadMemberSummary { Name = "Known Bot", IsBot = true });
        viewModel.Teammates.Add(new SquadMemberSummary { IsBot = false });
        Render(window);

        var ownerGrid = view.FindControl<DataGrid>("OwnerEliminationsGrid")!;
        var squadGrid = view.FindControl<DataGrid>("SquadGrid")!;
        var squadNameHeader = GetColumnHeader(squadGrid, "Name");
        var ownerNameHeader = GetColumnHeader(ownerGrid, "Name");
        var squadLevelHeader = GetColumnHeader(squadGrid, "Level");
        var ownerLevelHeader = GetColumnHeader(ownerGrid, "Level");
        var teammateLink = squadGrid.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.DataContext is SquadMemberSummary { Name: "Teammate" });
        var botNames = squadGrid.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(textBlock => textBlock.DataContext is SquadMemberSummary { Name: "Known Bot" })
            .ToArray();
        var missingNames = squadGrid.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(textBlock => textBlock.DataContext is SquadMemberSummary { Name: null })
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(squadGrid.Columns.Select(column => column.Header), Does.Not.Contain("Bot"));
            Assert.That(squadGrid.Columns.Single(column => Equals(column.Header, "Name")).ActualWidth,
                Is.EqualTo(ownerGrid.Columns.Single(column => Equals(column.Header, "Name")).ActualWidth));
            Assert.That(squadNameHeader.TranslatePoint(default, window)!.Value.X,
                Is.EqualTo(ownerNameHeader.TranslatePoint(default, window)!.Value.X).Within(0.1));
            Assert.That(squadLevelHeader.TranslatePoint(default, window)!.Value.X,
                Is.EqualTo(ownerLevelHeader.TranslatePoint(default, window)!.Value.X).Within(0.1));
            Assert.That(teammateLink.IsVisible, Is.True);
            Assert.That(botNames.Any(textBlock => textBlock.IsVisible), Is.True);
            Assert.That(missingNames.Any(textBlock => textBlock.IsVisible), Is.True);
            Assert.That(ToolTip.GetTip(teammateLink), Is.EqualTo("Click to check if this player has a Fortnite Tracker page"));
            Assert.That(MatchView.GetFortniteTrackerUrl(teammateLink.DataContext),
                Is.EqualTo("https://fortnitetracker.com/profile/all/Teammate"));
            Assert.That(MatchView.GetFortniteTrackerUrl(new SquadMemberSummary { Name = "Name With Space" }),
                Is.EqualTo("https://fortnitetracker.com/profile/all/Name%20With%20Space"));
            Assert.That(MatchView.GetFortniteTrackerUrl(new SquadMemberSummary { Name = "Bot", IsBot = true }), Is.Null);
            Assert.That(MatchView.GetFortniteTrackerUrl(new SquadMemberSummary { IsBot = false }), Is.Null);
        });
        window.Close();
    }

    private static DataGridColumnHeader GetColumnHeader(DataGrid grid, string header) =>
        grid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .Single(columnHeader => Equals(columnHeader.Content, header));

    private static void Render(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
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
            => _next < replays.Length ? Task.FromResult(replays[_next++])
                : Task.FromException<ReplayData>(new InvalidDataException("sample failure"));
    }
}
