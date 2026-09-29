using System.Reactive.Linq;
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
public sealed class MatchMetadataBindingTests
{
    [AvaloniaTest]
    public async Task MatchmakingRegion_RendersInSummaryBar()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"botornot-match-region-{Guid.NewGuid():N}.json");
        var replay = new ReplayData
        {
            Metadata = new ReplayMetadata
            {
                FileName = "region.replay",
                MatchmakingRegion = "NAW"
            }
        };
        var viewModel = new MainWindowViewModel(
            replayService: new FixedReplayService(replay),
            themeService: new ThemeService(new SettingsService(settingsPath)));
        await viewModel.LoadReplayCommand.Execute("region.replay").FirstAsync();

        var view = new MatchView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.That(view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text),
                Does.Contain("Region: NAW"));
        }
        finally
        {
            window.Close();
            File.Delete(settingsPath);
        }
    }

    private sealed class FixedReplayService(ReplayData replay) : IReplayService
    {
        public Task<ReplayData> LoadReplayAsync(
            string path,
            CancellationToken cancellationToken = default) => Task.FromResult(replay);
    }
}
