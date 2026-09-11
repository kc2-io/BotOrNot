using System.Reactive.Linq;
using Avalonia.Headless.NUnit;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Services;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class LibraryViewModelTests
{
    [AvaloniaTest]
    public async Task Scan_WithNoKnownKillCounts_DisplaysUnknownAverage()
    {
        var cache = new StubReplayCacheService(
        [
            new ReplaySummary
            {
                FileName = "owner-unknown.replay",
                AnalysisStatus = ReplayAnalysisStatus.OwnerIdentityUnavailable,
                Kills = null
            },
            new ReplaySummary
            {
                FileName = "kills-unknown.replay",
                AnalysisStatus = ReplayAnalysisStatus.OwnerKillsUnavailable,
                Kills = null
            }
        ]);
        var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService())
        {
            DirectoryPath = Path.GetTempPath()
        };

        await viewModel.ScanCommand.Execute().FirstAsync();

        Assert.That(viewModel.AvgKills, Is.Null);
        Assert.That(viewModel.AvgKillsDisplay, Is.EqualTo("Unknown"));
    }

    [AvaloniaTest]
    public async Task Scan_AveragesOnlyAuthoritativeKillCounts()
    {
        var cache = new StubReplayCacheService(
        [
            new ReplaySummary { AnalysisStatus = ReplayAnalysisStatus.Complete, Kills = 0 },
            new ReplaySummary { AnalysisStatus = ReplayAnalysisStatus.Complete, Kills = 6 },
            new ReplaySummary { AnalysisStatus = ReplayAnalysisStatus.OwnerKillsUnavailable, Kills = null }
        ]);
        var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService())
        {
            DirectoryPath = Path.GetTempPath()
        };

        await viewModel.ScanCommand.Execute().FirstAsync();

        Assert.That(viewModel.AvgKills, Is.EqualTo(3));
        Assert.That(viewModel.AvgKillsDisplay, Is.EqualTo("3.0"));
    }

    private sealed class StubReplayCacheService(IReadOnlyList<ReplaySummary> summaries) : IReplayCacheService
    {
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
            string directory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(summaries);
    }

    private static ISettingsService CreateSettingsService() => new SettingsService(
        Path.Combine(Path.GetTempPath(), $"botornot-{Guid.NewGuid():N}.json"));
}
