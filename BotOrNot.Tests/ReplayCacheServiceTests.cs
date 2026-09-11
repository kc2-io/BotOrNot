using System.Collections.Concurrent;
using System.Text.Json;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class ReplayCacheServiceTests
{
    private string _directory = null!;
    private string _cachePath = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "BotOrNot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _cachePath = Path.Combine(_directory, "replay-cache.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task LegacyCacheEntry_IsReparsedAndCurrentEntryIsReused()
    {
        var replayPath = CreateReplay("match.replay");
        var file = new FileInfo(replayPath);
        var legacyKey = $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var legacyCache = new Dictionary<string, ReplaySummary>
        {
            [legacyKey] = new() { FileName = file.Name, Kills = 0, DurationMinutes = 0 }
        };
        File.WriteAllText(_cachePath, JsonSerializer.Serialize(legacyCache));

        var initialService = new CountingReplayService(CompleteData(kills: 0, durationMinutes: 8.5));
        var initialCache = new ReplayCacheService(initialService, cachePath: _cachePath);

        var initial = await initialCache.GetSummariesAsync(_directory);

        Assert.That(initialService.CallCount, Is.EqualTo(1), "A legacy entry must not be trusted after an analysis revision change.");
        Assert.That(initial.Single().Kills, Is.EqualTo(0), "A resolved zero-kill match remains a real zero.");
        Assert.That(initial.Single().AnalysisStatus, Is.EqualTo(ReplayAnalysisStatus.Complete));
        Assert.That(initial.Single().DurationMinutes, Is.EqualTo(8.5));

        var cachedJson = File.ReadAllText(_cachePath);
        Assert.That(cachedJson, Does.Contain(ReplayCacheService.AnalysisRevision));
        Assert.That(cachedJson, Does.Not.Contain($"\"{legacyKey}\""));

        var reuseService = new CountingReplayService(CompleteData(kills: 99, durationMinutes: 99));
        var reuseCache = new ReplayCacheService(reuseService, cachePath: _cachePath);

        var reused = await reuseCache.GetSummariesAsync(_directory);

        Assert.That(reuseService.CallCount, Is.Zero, "An entry at the current analysis revision should be reused.");
        Assert.That(reused.Single().Kills, Is.EqualTo(0));
        Assert.That(reused.Single().DurationMinutes, Is.EqualTo(8.5));
    }

    [Test]
    public async Task MissingOwnerIdentity_IsExposedAsUnknownInsteadOfZeroKills()
    {
        CreateReplay("incomplete.replay");
        var replayService = new CountingReplayService(new ReplayData
        {
            OwnerKills = 0,
            Metadata = new ReplayMetadata { RecordingDurationMinutes = 3.4 }
        });
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        var summary = (await cache.GetSummariesAsync(_directory)).Single();

        Assert.That(summary.AnalysisStatus, Is.EqualTo(ReplayAnalysisStatus.OwnerIdentityUnavailable));
        Assert.That(summary.Kills, Is.Null);
        Assert.That(summary.BotKills, Is.Null);
        Assert.That(summary.PlayerKills, Is.Null);
        Assert.That(summary.AnalysisStatusText, Is.EqualTo("Owner unknown"));
    }

    [Test]
    public async Task MissingOwnerKillField_IsExposedAsUnknown()
    {
        CreateReplay("partial.replay");
        var replayService = new CountingReplayService(new ReplayData
        {
            OwnerId = "owner",
            OwnerTeamIndex = 4,
            OwnerName = "Recorder",
            Players =
            [
                new PlayerRow
                {
                    StableId = "owner",
                    Id = "owner",
                    Name = "Recorder",
                    TeamIndexValue = 4,
                    IsReplayOwner = true,
                    Bot = "false"
                }
            ],
            Metadata = new ReplayMetadata { RecordingDurationMinutes = 3.4 }
        });
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        var summary = (await cache.GetSummariesAsync(_directory)).Single();

        Assert.That(summary.AnalysisStatus, Is.EqualTo(ReplayAnalysisStatus.OwnerKillsUnavailable));
        Assert.That(summary.Kills, Is.Null);
        Assert.That(summary.BotKills, Is.Null);
    }

    [Test]
    public async Task ScanAsync_DefaultsToNewest50AndReportsAvailableSelectedAndProcessed()
    {
        var newest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var initialTime = DateTime.UtcNow.AddDays(-1);
        for (var index = 0; index < 76; index++)
        {
            var path = CreateReplay($"match-{index:D2}.replay");
            File.SetLastWriteTimeUtc(path, initialTime.AddMinutes(index));
            if (index >= 26)
                newest.Add(Path.GetFullPath(path));
        }
        var replayService = new RecordingReplayService(_ => Task.FromResult(CompleteData(1, 1)));
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        var updates = await CollectAsync(cache.ScanAsync(_directory));

        Assert.Multiple(() =>
        {
            Assert.That(updates.First().Status, Is.EqualTo(ReplayScanStatus.Started));
            Assert.That(updates.First().AvailableCount, Is.EqualTo(76));
            Assert.That(updates.First().SelectedCount, Is.EqualTo(50));
            Assert.That(updates.First().ConfiguredLimit, Is.EqualTo(50));
            Assert.That(replayService.CallCount, Is.EqualTo(50));
            Assert.That(replayService.Paths, Is.EquivalentTo(newest));
            Assert.That(updates.Last().Status, Is.EqualTo(ReplayScanStatus.Completed));
            Assert.That(updates.Last().ProcessedCount, Is.EqualTo(50));
            Assert.That(updates.Last().LoadedCount, Is.EqualTo(50));
            Assert.That(updates.Last().FailedCount, Is.Zero);
            Assert.That(updates.Last().ProgressPercentage, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task ScanAsync_PublishesCacheHitsFirstAndRetainsExcludedEntries()
    {
        var time = DateTime.UtcNow.AddMinutes(-1);
        for (var index = 0; index < 3; index++)
        {
            var path = CreateReplay($"match-{index}.replay");
            File.SetLastWriteTimeUtc(path, time.AddSeconds(index));
        }
        var replayService = new RecordingReplayService(_ => Task.FromResult(CompleteData(1, 1)));
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        await CollectAsync(cache.ScanAsync(_directory, new ReplayScanOptions { Limit = 2 }));
        var second = await CollectAsync(cache.ScanAsync(_directory, new ReplayScanOptions { Limit = 3 }));
        var itemUpdates = second.Where(update => update.Status is ReplayScanStatus.Cached or ReplayScanStatus.Loaded).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(replayService.CallCount, Is.EqualTo(3), "Only the newly selected third file should be decoded.");
            Assert.That(itemUpdates.Select(update => update.Status), Is.EqualTo(new[]
            {
                ReplayScanStatus.Cached,
                ReplayScanStatus.Cached,
                ReplayScanStatus.Loaded
            }));
            Assert.That(second.Last().LoadedCount, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task ScanAsync_FailedFileIsProcessedButNotLoaded()
    {
        CreateReplay("good.replay");
        CreateReplay("bad.replay");
        var replayService = new RecordingReplayService(path =>
            Path.GetFileName(path) == "bad.replay"
                ? Task.FromException<ReplayData>(new InvalidDataException("corrupt"))
                : Task.FromResult(CompleteData(1, 1)));
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        var updates = await CollectAsync(cache.ScanAsync(_directory));
        var failed = updates.Single(update => update.Status == ReplayScanStatus.Failed);

        Assert.Multiple(() =>
        {
            Assert.That(failed.ErrorMessage, Does.Contain("corrupt"));
            Assert.That(updates.Last().ProcessedCount, Is.EqualTo(2));
            Assert.That(updates.Last().LoadedCount, Is.EqualTo(1));
            Assert.That(updates.Last().FailedCount, Is.EqualTo(1));
            Assert.That(updates.Last().ProgressPercentage, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task CacheIdentityIncludesFullPath()
    {
        var otherDirectory = Path.Combine(_directory, "other");
        Directory.CreateDirectory(otherDirectory);
        var first = CreateReplay("same.replay");
        var second = Path.Combine(otherDirectory, "same.replay");
        File.WriteAllBytes(second, [1]);
        var timestamp = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(first, timestamp);
        File.SetLastWriteTimeUtc(second, timestamp);
        var replayService = new RecordingReplayService(path => Task.FromResult(
            CompleteData(path.Contains("other", StringComparison.OrdinalIgnoreCase) ? 2 : 1, 1)));
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        var firstSummary = (await cache.GetSummariesAsync(_directory)).Single(summary => summary.FileName == "same.replay");
        var secondSummary = (await cache.GetSummariesAsync(otherDirectory)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(replayService.CallCount, Is.EqualTo(2));
            Assert.That(firstSummary.Kills, Is.EqualTo(1));
            Assert.That(secondSummary.Kills, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ScanAsync_EqualTimestampsUseStablePathOrdering()
    {
        var timestamp = DateTime.UtcNow.AddMinutes(-1);
        foreach (var name in new[] { "c.replay", "a.replay", "b.replay" })
        {
            var path = CreateReplay(name);
            File.SetLastWriteTimeUtc(path, timestamp);
        }
        var replayService = new RecordingReplayService(_ => Task.FromResult(CompleteData(1, 1)));
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        var updates = await CollectAsync(cache.ScanAsync(
            _directory, new ReplayScanOptions { Limit = 2, MaxConcurrency = 1 }));

        Assert.That(updates.Where(update => update.Summary is not null)
            .Select(update => update.File!.FileName), Is.EqualTo(new[] { "a.replay", "b.replay" }));
    }

    [Test]
    public async Task ScanAsync_NeverExceedsConfiguredWorkerConcurrency()
    {
        for (var index = 0; index < 8; index++)
            CreateReplay($"match-{index}.replay");
        var active = 0;
        var peak = 0;
        var replayService = new RecordingReplayService(async _ =>
        {
            var nowActive = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref peak, nowActive);
            try
            {
                await Task.Delay(25);
                return CompleteData(1, 1);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        await CollectAsync(cache.ScanAsync(
            _directory, new ReplayScanOptions { Limit = 8, MaxConcurrency = 2 }));

        Assert.That(peak, Is.EqualTo(2));
    }

    [Test]
    public async Task ScanAsync_EmptyFolderHasDefinedCompleteProgress()
    {
        var cache = new ReplayCacheService(new RecordingReplayService(_ => Task.FromResult(CompleteData(1, 1))),
            cachePath: _cachePath);

        var updates = await CollectAsync(cache.ScanAsync(_directory));

        Assert.Multiple(() =>
        {
            Assert.That(updates.Select(update => update.Status),
                Is.EqualTo(new[] { ReplayScanStatus.Started, ReplayScanStatus.Completed }));
            Assert.That(updates.Last().AvailableCount, Is.Zero);
            Assert.That(updates.Last().SelectedCount, Is.Zero);
            Assert.That(updates.Last().ProgressPercentage, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task ConcurrentServiceInstancesMergeAtomicCacheCheckpoints()
    {
        var firstDirectory = Path.Combine(_directory, "first");
        var secondDirectory = Path.Combine(_directory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        File.WriteAllBytes(Path.Combine(firstDirectory, "first.replay"), [1]);
        File.WriteAllBytes(Path.Combine(secondDirectory, "second.replay"), [2]);
        var replayService = new RecordingReplayService(async _ =>
        {
            await Task.Delay(20);
            return CompleteData(1, 1);
        });
        var firstCache = new ReplayCacheService(replayService, cachePath: _cachePath);
        var secondCache = new ReplayCacheService(replayService, cachePath: _cachePath);

        await Task.WhenAll(
            CollectAsync(firstCache.ScanAsync(firstDirectory)),
            CollectAsync(secondCache.ScanAsync(secondDirectory)));

        var rejectingService = new RecordingReplayService(_ =>
            Task.FromException<ReplayData>(new AssertionException("Cache entry was lost.")));
        var verifier = new ReplayCacheService(rejectingService, cachePath: _cachePath);
        var firstUpdates = await CollectAsync(verifier.ScanAsync(firstDirectory));
        var secondUpdates = await CollectAsync(verifier.ScanAsync(secondDirectory));

        Assert.Multiple(() =>
        {
            Assert.That(rejectingService.CallCount, Is.Zero);
            Assert.That(firstUpdates.Single(update => update.Summary is not null).Status,
                Is.EqualTo(ReplayScanStatus.Cached));
            Assert.That(secondUpdates.Single(update => update.Summary is not null).Status,
                Is.EqualTo(ReplayScanStatus.Cached));
            Assert.DoesNotThrow(() => JsonDocument.Parse(File.ReadAllText(_cachePath)));
        });
    }

    [Test]
    public async Task FileChangedDuringDecodeIsFailedAndNotCached()
    {
        var path = CreateReplay("active.replay");
        var firstService = new RecordingReplayService(file =>
        {
            using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.WriteByte(2);
            return Task.FromResult(CompleteData(1, 1));
        });
        var cache = new ReplayCacheService(firstService, cachePath: _cachePath);

        var firstUpdates = await CollectAsync(cache.ScanAsync(_directory));
        var secondService = new RecordingReplayService(_ => Task.FromResult(CompleteData(2, 1)));
        var secondCache = new ReplayCacheService(secondService, cachePath: _cachePath);
        var secondUpdates = await CollectAsync(secondCache.ScanAsync(_directory));

        Assert.Multiple(() =>
        {
            Assert.That(firstUpdates.Single(update => update.Status == ReplayScanStatus.Failed).ErrorMessage,
                Does.Contain("changed"));
            Assert.That(secondService.CallCount, Is.EqualTo(1));
            Assert.That(secondUpdates.Single(update => update.Summary is not null).Summary!.Kills, Is.EqualTo(2));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task DefaultInterfaceAdapterAppliesLimitAndPreservesAvailableCount()
    {
        var summaries = new[]
        {
            new ReplaySummary { FileName = "old.replay", FilePath = Path.Combine(_directory, "old.replay"), FileDate = DateTime.UtcNow.AddHours(-2) },
            new ReplaySummary { FileName = "new.replay", FilePath = Path.Combine(_directory, "new.replay"), FileDate = DateTime.UtcNow },
            new ReplaySummary { FileName = "middle.replay", FilePath = Path.Combine(_directory, "middle.replay"), FileDate = DateTime.UtcNow.AddHours(-1) }
        };
        IReplayCacheService legacy = new LegacyReplayCacheService(summaries);

        var updates = await CollectAsync(legacy.ScanAsync(
            _directory, new ReplayScanOptions { Limit = 2 }));

        Assert.Multiple(() =>
        {
            Assert.That(updates.First().AvailableCount, Is.EqualTo(3));
            Assert.That(updates.First().SelectedCount, Is.EqualTo(2));
            Assert.That(updates.Where(update => update.Summary is not null)
                .Select(update => update.Summary!.FileName),
                Is.EqualTo(new[] { "new.replay", "middle.replay" }));
        });
    }

    [Test]
    public async Task DisposingStreamStopsProducerAndWorkersWithoutDrainingBacklog()
    {
        for (var index = 0; index < 10; index++)
            CreateReplay($"match-{index}.replay");
        var replayService = new DisposeAwareReplayService(CompleteData(1, 1));
        var cache = new ReplayCacheService(replayService, cachePath: _cachePath);

        await foreach (var update in cache.ScanAsync(
            _directory, new ReplayScanOptions { Limit = 10, MaxConcurrency = 2 }))
        {
            if (update.Summary is not null)
                break;
        }

        await Task.Delay(25);
        using var cacheJson = JsonDocument.Parse(File.ReadAllText(_cachePath));
        Assert.Multiple(() =>
        {
            Assert.That(replayService.ActiveCount, Is.Zero, "Iterator disposal should cancel active service awaits.");
            Assert.That(replayService.CallCount, Is.LessThanOrEqualTo(3),
                "Only the delivered decode and the bounded in-flight workers may start.");
            Assert.That(cacheJson.RootElement.EnumerateObject().Count(), Is.EqualTo(1),
                "The completed row is checkpointed without draining queued files.");
        });
    }

    [Test]
    public void AlreadyCancelledScanDoesNotTouchDirectory()
    {
        var cache = new ReplayCacheService(cachePath: _cachePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await CollectAsync(cache.ScanAsync(
            Path.Combine(_directory, "does-not-exist"), cancellationToken: cancellation.Token)));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ScanAsync_RejectsNonPositiveLimits(int limit)
    {
        var cache = new ReplayCacheService(cachePath: _cachePath);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await CollectAsync(cache.ScanAsync(_directory, new ReplayScanOptions { Limit = limit })));
    }

    [Test]
    public async Task Summary_ContainsOnlyPerMatchDeduplicatedStableOpponents()
    {
        CreateReplay("opponents.replay");
        var replay = CompleteData(kills: 1, durationMinutes: 5);
        replay.Players.AddRange(
        [
            new PlayerRow { StableId = "mate", Id = "mate", Name = "Mate", TeamIndexValue = 4, Bot = "false" },
            new PlayerRow { StableId = "enemy", Id = "enemy", Name = "Enemy", TeamIndexValue = 8, Bot = "false" },
            new PlayerRow { StableId = "ENEMY", Id = "ENEMY", Name = "Enemy renamed", TeamIndexValue = 8, Bot = "false" },
            new PlayerRow { StableId = "bot", Id = "bot", Name = "Bot", TeamIndexValue = 8, Bot = "true" }
        ]);
        var cache = new ReplayCacheService(new CountingReplayService(replay), cachePath: _cachePath);

        var summary = (await cache.GetSummariesAsync(_directory)).Single();

        Assert.That(summary.Opponents, Has.Count.EqualTo(1));
        Assert.That(summary.Opponents.Single().StableId, Is.EqualTo("enemy").IgnoreCase);
        Assert.That(summary.OpponentAnalysisComplete, Is.True);
    }

    [Test]
    public void Summary_UsesOwnerFlagForPlacementInsteadOfDisplayName()
    {
        var replayPath = CreateReplay("placement.replay");
        var replay = CompleteData(kills: 0, durationMinutes: 5);
        replay.Players[0].Placement = "1";
        replay.Players.Insert(0, new PlayerRow
        {
            StableId = "different-account",
            Id = "different-account",
            Name = "Recorder",
            Placement = "99",
            TeamIndexValue = 8,
            Bot = "false"
        });

        var summary = ReplaySummaryFactory.Create(replay, new FileInfo(replayPath));

        Assert.That(summary.Placement, Is.EqualTo("1"));
    }

    private string CreateReplay(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    private static ReplayData CompleteData(int kills, double durationMinutes) => new()
    {
        OwnerId = "owner",
        OwnerTeamIndex = 4,
        OwnerName = "Recorder",
        OwnerKills = kills,
        Metadata = new ReplayMetadata { RecordingDurationMinutes = durationMinutes },
        Players =
        [
            new PlayerRow
            {
                StableId = "owner",
                Id = "owner",
                Name = "Recorder",
                TeamIndexValue = 4,
                IsReplayOwner = true,
                Bot = "false"
            }
        ]
    };

    private static async Task<List<ReplayScanUpdate>> CollectAsync(IAsyncEnumerable<ReplayScanUpdate> source)
    {
        var updates = new List<ReplayScanUpdate>();
        await foreach (var update in source)
            updates.Add(update);
        return updates;
    }

    private sealed class CountingReplayService(ReplayData data) : IReplayService
    {
        public int CallCount { get; private set; }

        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(data);
        }
    }

    private sealed class RecordingReplayService(Func<string, Task<ReplayData>> load) : IReplayService
    {
        private int _callCount;
        private readonly ConcurrentBag<string> _paths = [];

        public int CallCount => Volatile.Read(ref _callCount);
        public IReadOnlyCollection<string> Paths => _paths;

        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            _paths.Add(Path.GetFullPath(path));
            return load(path);
        }
    }

    private sealed class LegacyReplayCacheService(IReadOnlyList<ReplaySummary> summaries) : IReplayCacheService
    {
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
            string directory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(summaries);
    }

    private sealed class DisposeAwareReplayService(ReplayData data) : IReplayService
    {
        private int _callCount;
        private int _activeCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public int ActiveCount => Volatile.Read(ref _activeCount);

        public async Task<ReplayData> LoadReplayAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            Interlocked.Increment(ref _activeCount);
            try
            {
                if (call == 1)
                    return data;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return data;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
            }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var prior = Interlocked.CompareExchange(ref target, value, current);
                if (prior == current)
                    return;
                current = prior;
            }
        }
    }
}
