using System.Reactive.Linq;
using System.Runtime.CompilerServices;
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
    public async Task Scan_DiagnosticObserverSeesFinalStateAndConfiguredWorkers()
    {
        var cache = new OptionsRecordingCache();
        var observer = new RecordingObserver();
        using var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService(),
            () => new ReplayScanOptions { MaxConcurrency = 4 }, observer)
        {
            DirectoryPath = Path.GetTempPath()
        };

        viewModel.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => observer.Milestones.Contains(LibraryScanMilestone.ScanDrained));

        Assert.Multiple(() =>
        {
            Assert.That(cache.Options?.MaxConcurrency, Is.EqualTo(4));
            Assert.That(cache.WasDisposed, Is.True, "ScanDrained must follow enumerator disposal.");
            Assert.That(observer.Milestones, Does.Contain(LibraryScanMilestone.Invoked)
                .And.Contain(LibraryScanMilestone.FirstModelRow)
                .And.Contain(LibraryScanMilestone.FinalModelState)
                .And.Contain(LibraryScanMilestone.ScanDrained));
            Assert.That(observer.Milestones.IndexOf(LibraryScanMilestone.FinalModelState),
                Is.LessThan(observer.Milestones.IndexOf(LibraryScanMilestone.ScanDrained)));
            Assert.That(viewModel.IsScanning, Is.False);
            Assert.That(viewModel.TotalMatches, Is.EqualTo(1));
        });
    }
    [AvaloniaTest]
    public async Task Scan_FlushesCachedRowsWhileNextDecodeIsPending()
    {
        var cache = new CachedBatchThenSlowDecode();
        using var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService())
        {
            DirectoryPath = Path.GetTempPath()
        };
        viewModel.ScanCommand.Execute().Subscribe();
        await cache.WaitingForDecode.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => viewModel.Replays.Count == 2);
        Assert.That(viewModel.IsScanning, Is.True, "Cached rows should not wait for the slow decode.");
        cache.ReleaseDecode.TrySetResult();
        await WaitForAsync(() => !viewModel.IsScanning);
    }

    [AvaloniaTest]
    public async Task Scan_ShowsFirstReplayAndPartialTotalsBeforeTheStreamCompletes()
    {
        var cache = new FirstRowControlledCacheService();
        using var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService())
        {
            DirectoryPath = Path.GetTempPath()
        };

        viewModel.ScanCommand.Execute().Subscribe(_ => { }, _ => { });

        await cache.FirstRowWasObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.IsScanning, Is.True);
            Assert.That(viewModel.Replays, Has.Count.EqualTo(1));
            Assert.That(viewModel.TotalMatches, Is.EqualTo(1));
            Assert.That(viewModel.ScanStatusText, Is.EqualTo("Scanned 1 out of 2 replay files"));
        });

        cache.ReleaseCompletion.SetResult();
        await WaitForAsync(() => !viewModel.IsScanning && viewModel.Replays.Count == 2);
        Assert.That(viewModel.Replays, Has.Count.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task Scan_IgnoresLateRowsFromASupersededDirectory()
    {
        var cache = new SupersededScanCacheService();
        using var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService())
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "first")
        };

        viewModel.ReplayScanLimitText = "50";
        viewModel.ApplyScanLimitCommand.Execute().Subscribe(_ => { }, _ => { });
        await cache.FirstRowWasObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.DirectoryPath = Path.Combine(Path.GetTempPath(), "second");
        viewModel.ReplayScanLimitText = "76";
        viewModel.ApplyScanLimitCommand.Execute().Subscribe(_ => { }, _ => { });
        await WaitForAsync(() => viewModel.Replays.Any(replay => replay.FileName == "second.replay"));
        cache.ReleaseStaleRow.SetResult();

        await cache.FirstScanFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.Replays.Select(replay => replay.FileName), Is.EquivalentTo(new[] { "second.replay" }));
            Assert.That(viewModel.TotalMatches, Is.EqualTo(1));
        });
    }

    [AvaloniaTest]
    public async Task ApplyScanLimit_PersistsOnlyPositiveWholeNumbersAndPreservesOtherSettings()
    {
        var settings = CreateSettingsService();
        settings.Save(new AppSettings
        {
            Theme = ThemePreference.Dark,
            ReplayDirectory = Path.GetTempPath(),
            ReplayScanLimit = 7
        });
        using var viewModel = new LibraryViewModel(_ => { }, new StubReplayCacheService([]), settings);

        foreach (var invalid in new[] { "0", "-1", "abc", "1.5", "2147483648", "" })
        {
            viewModel.ReplayScanLimitText = invalid;
            viewModel.ApplyScanLimitCommand.Execute().Subscribe(_ => { }, _ => { });
            Assert.That(viewModel.ReplayScanLimit, Is.EqualTo(7), invalid);
            Assert.That(settings.Load().ReplayScanLimit, Is.EqualTo(7), invalid);
        }

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.ReplayScanLimit, Is.EqualTo(7));
            Assert.That(settings.Load().ReplayScanLimit, Is.EqualTo(7));
            Assert.That(settings.Load().Theme, Is.EqualTo(ThemePreference.Dark));
            Assert.That(settings.Load().ReplayDirectory, Is.EqualTo(Path.GetTempPath()));
        });

        viewModel.ReplayScanLimitText = "76";
        viewModel.ApplyScanLimitCommand.Execute().Subscribe(_ => { }, _ => { });
        await WaitForAsync(() => !viewModel.IsScanning);
        Assert.That(settings.Load().ReplayScanLimit, Is.EqualTo(76));
    }

    [AvaloniaTest]
    public async Task Scan_UsesSelectedDenominatorForProgressAndKeepsFailuresSeparate()
    {
        var cache = new FixedStreamCacheService(
        [
            Update(ReplayScanStatus.Started, processed: 0, loaded: 0, failed: 0),
            Update(ReplayScanStatus.Loaded, processed: 1, loaded: 1, failed: 0, summary: Summary("first.replay")),
            Update(ReplayScanStatus.Failed, processed: 50, loaded: 48, failed: 2, error: "corrupt"),
            Update(ReplayScanStatus.Completed, processed: 50, loaded: 48, failed: 2)
        ]);
        using var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService())
        {
            DirectoryPath = Path.GetTempPath()
        };

        await ScanToRowsAsync(viewModel, 1);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.ScanProgress, Is.EqualTo(100));
            Assert.That(viewModel.ScanStatusText, Is.EqualTo("Scanned 50 out of 76 replay files"));
            Assert.That(viewModel.ScanOutcomeText, Is.EqualTo("48 loaded, 2 failed"));
            Assert.That(viewModel.LoadedReplayCount, Is.EqualTo(48));
            Assert.That(viewModel.FailedReplayCount, Is.EqualTo(2));
            Assert.That(viewModel.OpponentDataIncompleteText, Is.EqualTo("Opponent data incomplete for 1 match"));
        });
    }

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

        await ScanToRowsAsync(viewModel, 2);

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

        await ScanToRowsAsync(viewModel, 3);

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
        var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService()) { DirectoryPath = Path.GetTempPath() };

        await ScanToRowsAsync(viewModel, 2);

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
        var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService()) { DirectoryPath = Path.GetTempPath() };

        await ScanToRowsAsync(viewModel, 1);

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
        var viewModel = new LibraryViewModel(_ => { }, cache, CreateSettingsService()) { DirectoryPath = Path.GetTempPath() };

        await ScanToRowsAsync(viewModel, 2);

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

    private sealed class RecordingObserver : ILibraryScanObserver
    {
        public List<LibraryScanMilestone> Milestones { get; } = [];
        public void OnMilestone(LibraryScanMilestone milestone) => Milestones.Add(milestone);
    }

    private sealed class OptionsRecordingCache : IReplayCacheService
    {
        public ReplayScanOptions? Options { get; private set; }
        public bool WasDisposed { get; private set; }
        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            try
            {
                yield return Update(ReplayScanStatus.Started, 0, 0, 0, available: 1, selected: 1);
                yield return Update(ReplayScanStatus.Loaded, 1, 1, 0, Summary("benchmark.replay"), available: 1, selected: 1);
                yield return Update(ReplayScanStatus.Completed, 1, 1, 0, available: 1, selected: 1);
                await Task.CompletedTask;
            }
            finally
            {
                WasDisposed = true;
            }
        }
    }

    private sealed class StubReplayCacheService(IReadOnlyList<ReplaySummary> summaries) : IReplayCacheService
    {
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
            string directory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(summaries);

        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
            string directory,
            ReplayScanOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var selected = summaries.Take(options?.Limit ?? ReplayScanOptions.DefaultLimit).ToList();
            yield return Update(ReplayScanStatus.Started, 0, 0, 0,
                available: summaries.Count, selected: selected.Count);

            var processed = 0;
            foreach (var summary in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                yield return Update(ReplayScanStatus.Loaded, processed, processed, 0, summary,
                    available: summaries.Count, selected: selected.Count);
                await Task.Yield();
            }

            yield return Update(ReplayScanStatus.Completed, processed, processed, 0,
                available: summaries.Count, selected: selected.Count);
        }
    }

    private sealed class FixedStreamCacheService(IReadOnlyList<ReplayScanUpdate> updates) : IReplayCacheService
    {
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
            string directory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReplaySummary>>([]);

        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
            string directory,
            ReplayScanOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }
    }

    private sealed class CachedBatchThenSlowDecode : IReplayCacheService
    {
        public TaskCompletionSource WaitingForDecode { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDecode { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Update(ReplayScanStatus.Started, 0, 0, 0);
            yield return Update(ReplayScanStatus.Cached, 1, 1, 0, Summary("one.replay"));
            yield return Update(ReplayScanStatus.Cached, 2, 2, 0, Summary("two.replay"));
            WaitingForDecode.TrySetResult();
            await ReleaseDecode.Task.WaitAsync(cancellationToken);
            yield return Update(ReplayScanStatus.Completed, 2, 2, 0);
        }
    }

    private sealed class FirstRowControlledCacheService : IReplayCacheService
    {
        public TaskCompletionSource FirstRowWasObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
            string directory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReplaySummary>>([]);

        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
            string directory,
            ReplayScanOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Update(ReplayScanStatus.Started, 0, 0, 0, available: 2, selected: 2);
            yield return Update(ReplayScanStatus.Loaded, 1, 1, 0, Summary("first.replay"), available: 2, selected: 2);
            FirstRowWasObserved.SetResult();
            await ReleaseCompletion.Task.WaitAsync(cancellationToken);
            yield return Update(ReplayScanStatus.Loaded, 2, 2, 0, Summary("second.replay"), available: 2, selected: 2);
            yield return Update(ReplayScanStatus.Completed, 2, 2, 0, available: 2, selected: 2);
        }
    }

    private sealed class SupersededScanCacheService : IReplayCacheService
    {
        private int _scanCount;
        public TaskCompletionSource FirstRowWasObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStaleRow { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstScanFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
            string directory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReplaySummary>>([]);

        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
            string directory,
            ReplayScanOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _scanCount) == 1)
            {
                yield return Update(ReplayScanStatus.Started, 0, 0, 0);
                yield return Update(ReplayScanStatus.Loaded, 1, 1, 0, Summary("first.replay"));
                FirstRowWasObserved.SetResult();
                await ReleaseStaleRow.Task;
                FirstScanFinished.TrySetResult();
                yield return Update(ReplayScanStatus.Loaded, 2, 2, 0, Summary("stale.replay"));
                yield return Update(ReplayScanStatus.Completed, 2, 2, 0);
                yield break;
            }

            yield return Update(ReplayScanStatus.Started, 0, 0, 0);
            yield return Update(ReplayScanStatus.Loaded, 1, 1, 0, Summary("second.replay"));
            yield return Update(ReplayScanStatus.Completed, 1, 1, 0);
        }
    }

    private static ReplaySummary Summary(string fileName) => new()
    {
        FileName = fileName,
        FilePath = Path.Combine(Path.GetTempPath(), fileName),
        FileDate = DateTime.UtcNow
    };

    private static ReplayScanUpdate Update(
        ReplayScanStatus status,
        int processed,
        int loaded,
        int failed,
        ReplaySummary? summary = null,
        string? error = null,
        int available = 76,
        int selected = 50) => new()
    {
        ScanId = Guid.NewGuid(),
        Status = status,
        ConfiguredLimit = 50,
        AvailableCount = available,
        SelectedCount = selected,
        ProcessedCount = processed,
        LoadedCount = loaded,
        FailedCount = failed,
        Summary = summary,
        ErrorMessage = error
    };

    private static async Task ScanToRowsAsync(LibraryViewModel viewModel, int expectedRows)
    {
        viewModel.ScanCommand.Execute().Subscribe(_ => { }, _ => { });
        await WaitForAsync(() => viewModel.Replays.Count == expectedRows);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the scan state.");
    }

    private static ISettingsService CreateSettingsService() => new SettingsService(
        Path.Combine(Path.GetTempPath(), $"botornot-{Guid.NewGuid():N}.json"));
}
