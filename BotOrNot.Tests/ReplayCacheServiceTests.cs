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

    private sealed class CountingReplayService(ReplayData data) : IReplayService
    {
        public int CallCount { get; private set; }

        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(data);
        }
    }
}
