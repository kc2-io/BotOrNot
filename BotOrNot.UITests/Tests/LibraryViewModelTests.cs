using System.Reactive.Linq;
using Avalonia.Headless.NUnit;
using BotOrNot.Avalonia.ViewModels;
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
        var viewModel = new LibraryViewModel(_ => { }, cache)
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
        var viewModel = new LibraryViewModel(_ => { }, cache)
        {
            DirectoryPath = Path.GetTempPath()
        };

        await viewModel.ScanCommand.Execute().FirstAsync();

        Assert.That(viewModel.AvgKills, Is.EqualTo(3));
        Assert.That(viewModel.AvgKillsDisplay, Is.EqualTo("3.0"));
    }

    [AvaloniaTest]
    public async Task Scan_CountsDistinctMatchesByStableIdentityAndUsesLatestName()
    {
        var older = DateTime.UtcNow.AddDays(-1);
        var newer = DateTime.UtcNow;
        var cache = new StubReplayCacheService(
        [
            new ReplaySummary
            {
                FileDate = older,
                Opponents =
                [
                    new OpponentSummary { StableId = "returning", Name = "Old name" },
                    new OpponentSummary { StableId = "same-name-a", Name = "Shared name" }
                ]
            },
            new ReplaySummary
            {
                FileDate = newer,
                Opponents =
                [
                    new OpponentSummary { StableId = "RETURNING", Name = "New name" },
                    new OpponentSummary { StableId = "returning", Name = "New name duplicate" },
                    new OpponentSummary { StableId = "same-name-b", Name = "Shared name" }
                ]
            }
        ]);
        var viewModel = new LibraryViewModel(_ => { }, cache) { DirectoryPath = Path.GetTempPath() };

        await viewModel.ScanCommand.Execute().FirstAsync();

        Assert.That(viewModel.FrequentOpponents, Has.Count.EqualTo(3));
        var returning = viewModel.FrequentOpponents.Single(opponent =>
            opponent.StableId.Equals("returning", StringComparison.OrdinalIgnoreCase));
        Assert.That(returning.Appearances, Is.EqualTo(2));
        Assert.That(returning.Name, Is.EqualTo("New name"));
        Assert.That(
            viewModel.FrequentOpponents.Count(opponent => opponent.Name == "Shared name"),
            Is.EqualTo(2),
            "Equal display names with different stable IDs must remain separate identities.");
    }

    [AvaloniaTest]
    public async Task Scan_DeterministicallyLimitsTiedOpponentsToTen()
    {
        var cache = new StubReplayCacheService(
        [
            new ReplaySummary
            {
                Opponents = Enumerable.Range(0, 12)
                    .Select(index => new OpponentSummary
                    {
                        StableId = $"id-{index:D2}",
                        Name = "Same name"
                    })
                    .Reverse()
                    .ToList()
            }
        ]);
        var viewModel = new LibraryViewModel(_ => { }, cache) { DirectoryPath = Path.GetTempPath() };

        await viewModel.ScanCommand.Execute().FirstAsync();

        Assert.That(
            viewModel.FrequentOpponents.Select(opponent => opponent.StableId),
            Is.EqualTo(Enumerable.Range(0, 10).Select(index => $"id-{index:D2}")));
    }

    [AvaloniaTest]
    public async Task Scan_CountsReturningPlayerOnlyInOpponentMatch()
    {
        var teammateMatch = ReplayWithReturningPlayer(ownerTeam: 4, returningPlayerTeam: 4);
        var opponentMatch = ReplayWithReturningPlayer(ownerTeam: 9, returningPlayerTeam: 12);
        var cache = new StubReplayCacheService(
        [
            new ReplaySummary { Opponents = OpponentProjection.FromReplay(teammateMatch).Opponents },
            new ReplaySummary { Opponents = OpponentProjection.FromReplay(opponentMatch).Opponents }
        ]);
        var viewModel = new LibraryViewModel(_ => { }, cache) { DirectoryPath = Path.GetTempPath() };

        await viewModel.ScanCommand.Execute().FirstAsync();

        Assert.That(viewModel.FrequentOpponents, Has.Count.EqualTo(1));
        Assert.That(viewModel.FrequentOpponents.Single().StableId, Is.EqualTo("returning"));
        Assert.That(viewModel.FrequentOpponents.Single().Appearances, Is.EqualTo(1));
    }

    private static ReplayData ReplayWithReturningPlayer(int ownerTeam, int returningPlayerTeam) => new()
    {
        OwnerId = "owner",
        OwnerTeamIndex = ownerTeam,
        Players =
        [
            new PlayerRow
            {
                StableId = "owner",
                Id = "owner",
                Name = "Recorder",
                TeamIndexValue = ownerTeam,
                IsReplayOwner = true,
                Bot = "false"
            },
            new PlayerRow
            {
                StableId = "returning",
                Id = "returning",
                Name = "Returning player",
                TeamIndexValue = returningPlayerTeam,
                Bot = "false"
            }
        ]
    };

    private sealed class StubReplayCacheService(IReadOnlyList<ReplaySummary> summaries) : IReplayCacheService
    {
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
            string directory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(summaries);
    }
}
