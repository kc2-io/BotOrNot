using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
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
public sealed class MatchNpcSectionTests
{
    private string _settingsPath = null!;

    [SetUp]
    public void SetUp() =>
        _settingsPath = Path.Combine(Path.GetTempPath(), $"botornot-npc-ui-{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() => File.Delete(_settingsPath);

    [AvaloniaTest]
    public async Task NpcSection_RendersBelowPlayersAndExportsOnlyVisibleNpcColumns()
    {
        var replay = new ReplayData
        {
            Metadata = new ReplayMetadata { FileName = "npcs.replay" },
            Players =
            [
                new PlayerRow
                {
                    StableId = "player-id",
                    Id = "player-id",
                    Name = "Player",
                    Bot = "false"
                },
                new PlayerRow
                {
                    Id = "Wolf",
                    Name = "Wolf",
                    Bot = "true",
                    DeathCause = "Rifle"
                },
                new PlayerRow
                {
                    Id = "Boss",
                    Name = "Boss",
                    Bot = "true",
                    DeathCause = "Mythic"
                }
            ],
            OwnerEliminations =
            [
                new PlayerRow
                {
                    Id = "Boss",
                    Name = "Boss",
                    Bot = "true"
                }
            ]
        };
        var viewModel = new MainWindowViewModel(
            replayService: new FixedReplayService(replay),
            themeService: new ThemeService(new SettingsService(_settingsPath)));
        await viewModel.LoadReplayCommand.Execute("npcs.replay").FirstAsync();

        var view = new MatchView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        try
        {
            window.Show();
            Render(window);

            var playersGrid = view.FindControl<DataGrid>("PlayersGrid")!;
            var npcsGrid = view.FindControl<DataGrid>("NpcsGrid")!;
            var expander = view.GetVisualDescendants().OfType<Expander>().Single();

            Assert.Multiple(() =>
            {
                Assert.That(expander.IsVisible, Is.True);
                Assert.That(expander.IsExpanded, Is.False);
                Assert.That((expander.Header as TextBlock)?.Text, Is.EqualTo("NPCs Seen (2)"));
                Assert.That(npcsGrid.ItemsSource!.Cast<PlayerRow>().Select(player => player.Name),
                    Is.EqualTo(new[] { "Boss", "Wolf" }));
                Assert.That(npcsGrid.ItemsSource!.Cast<PlayerRow>().Select(player => player.KilledByOwner),
                    Is.EqualTo(new[] { true, false }));
                Assert.That(npcsGrid.Columns.Select(column => column.Header),
                    Is.EqualTo(new[] { "Id", "Name", "Death Cause", "Killed by You" }));
                Assert.That(npcsGrid.Columns.Single(column => Equals(column.Header, "Id")).IsVisible, Is.False);
            });

            expander.IsExpanded = true;
            Render(window);
            Assert.That(
                npcsGrid.TranslatePoint(default, window)!.Value.Y,
                Is.GreaterThan(playersGrid.TranslatePoint(default, window)!.Value.Y));

            var columns = MatchView.GetVisibleCsvColumns(npcsGrid);
            Assert.That(columns.Select(column => column.Header),
                Is.EqualTo(new[] { "Name", "Death Cause", "Killed by You" }));
            Assert.That(
                CsvExportService.GenerateCsv(viewModel.Npcs, columns),
                Is.EqualTo(
                    $"Name,Death Cause,Killed by You{Environment.NewLine}" +
                    $"Boss,Mythic,Yes{Environment.NewLine}" +
                    $"Wolf,Rifle,No{Environment.NewLine}"));

            DataGridTestHelper.ClickColumnHeader(window, npcsGrid, "Killed by You");
            Assert.That(
                DataGridTestHelper.GetDisplayedValues(npcsGrid, player => player.Name),
                Is.EqualTo(new[] { "Wolf", "Boss" }));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ColumnsFlyout_ControlsEachGridIndependentlyAndShowAllRestoresBoth()
    {
        var replay = new ReplayData
        {
            Metadata = new ReplayMetadata { FileName = "npcs.replay" },
            Players =
            [
                new PlayerRow
                {
                    StableId = "player-id",
                    Id = "player-id",
                    Name = "Player",
                    Bot = "false"
                },
                new PlayerRow
                {
                    Id = "Wolf",
                    Name = "Wolf",
                    Bot = "true",
                    DeathCause = "Rifle"
                }
            ]
        };
        var viewModel = new MainWindowViewModel(
            replayService: new FixedReplayService(replay),
            themeService: new ThemeService(new SettingsService(_settingsPath)));
        await viewModel.LoadReplayCommand.Execute("npcs.replay").FirstAsync();

        var view = new MatchView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        try
        {
            window.Show();
            Render(window);

            var playersGrid = view.FindControl<DataGrid>("PlayersGrid")!;
            var npcsGrid = view.FindControl<DataGrid>("NpcsGrid")!;
            var flyout = (MenuFlyout)view.FindControl<Button>("ColumnsButton")!.Flyout!;

            Assert.That(
                flyout.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "NPCs")).IsEnabled,
                Is.False);

            Click(FindColumnItem(flyout, npcsGrid, "Name"));
            Assert.Multiple(() =>
            {
                Assert.That(FindColumn(npcsGrid, "Name").IsVisible, Is.False);
                Assert.That(FindColumn(playersGrid, "Name").IsVisible, Is.True);
            });

            Click(FindColumnItem(flyout, npcsGrid, "Killed by You"));
            Click(FindColumnItem(flyout, npcsGrid, "Death Cause"));
            Assert.That(
                FindColumn(npcsGrid, "Death Cause").IsVisible,
                Is.True,
                "Each grid must retain at least one visible column.");

            Click(FindColumnItem(flyout, playersGrid, "Name"));
            Assert.That(FindColumn(playersGrid, "Name").IsVisible, Is.False);

            Click(flyout.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Show All")));
            Assert.Multiple(() =>
            {
                Assert.That(playersGrid.Columns.All(column => column.IsVisible), Is.True);
                Assert.That(npcsGrid.Columns.All(column => column.IsVisible), Is.True);
            });
        }
        finally
        {
            window.Close();
        }
    }

    private static DataGridColumn FindColumn(DataGrid grid, string header) =>
        grid.Columns.Single(column => Equals(column.Header, header));

    private static MenuItem FindColumnItem(MenuFlyout flyout, DataGrid grid, string header) =>
        flyout.Items.OfType<MenuItem>().Single(item =>
            Equals(item.Header, header) &&
            item.Tag is ValueTuple<DataGrid, DataGridColumn> target &&
            ReferenceEquals(target.Item1, grid));

    private static void Click(MenuItem item) =>
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void Render(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class FixedReplayService(ReplayData replay) : IReplayService
    {
        public Task<ReplayData> LoadReplayAsync(
            string path,
            CancellationToken cancellationToken = default) => Task.FromResult(replay);
    }
}
