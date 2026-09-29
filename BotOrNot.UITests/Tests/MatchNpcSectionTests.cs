using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

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
                Assert.That((expander.Header as TextBlock)?.Text, Is.EqualTo("NPCs Seen (1)"));
                Assert.That(npcsGrid.ItemsSource!.Cast<PlayerRow>().Select(player => player.Name),
                    Is.EqualTo(new[] { "Wolf" }));
                Assert.That(npcsGrid.Columns.Select(column => column.Header),
                    Is.EqualTo(new[] { "Id", "Name", "Death Cause" }));
                Assert.That(npcsGrid.Columns.Single(column => Equals(column.Header, "Id")).IsVisible, Is.False);
            });

            expander.IsExpanded = true;
            Render(window);
            Assert.That(
                npcsGrid.TranslatePoint(default, window)!.Value.Y,
                Is.GreaterThan(playersGrid.TranslatePoint(default, window)!.Value.Y));

            var columns = MatchView.GetVisibleCsvColumns(npcsGrid);
            Assert.That(columns.Select(column => column.Header), Is.EqualTo(new[] { "Name", "Death Cause" }));
            Assert.That(
                CsvExportService.GenerateCsv(viewModel.Npcs, columns),
                Is.EqualTo($"Name,Death Cause{Environment.NewLine}Wolf,Rifle{Environment.NewLine}"));
        }
        finally
        {
            window.Close();
        }
    }

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
