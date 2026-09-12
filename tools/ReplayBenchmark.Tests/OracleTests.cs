using BotOrNot.Core.Models;
using NUnit.Framework;
using ReplayBenchmark;

namespace ReplayBenchmark.Tests;

[TestFixture]
public sealed class OracleTests
{
    [Test]
    public async Task Freeze_UsesProductionOrderAndMarksNewestFifty()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var alpha = CreateReplay(root, "alpha.replay", 1, DateTime.UtcNow.AddMinutes(-1));
            var beta = CreateReplay(root, "beta.replay", 2, DateTime.UtcNow);
            var manifest = await ReplayManifestService.FreezeAsync(root);

            Assert.Multiple(() =>
            {
                Assert.That(manifest.Entries.Select(entry => entry.RelativePath),
                    Is.EqualTo(new[] { Path.GetFileName(beta), Path.GetFileName(alpha) }));
                Assert.That(manifest.Entries.Select(entry => entry.AllRank), Is.EqualTo(new[] { 1, 2 }));
                Assert.That(manifest.Entries.Select(entry => entry.Newest50Rank), Is.EqualTo(new int?[] { 1, 2 }));
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Freeze_CanPreserveAnEarlierCorpusByTimestampBoundary()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cutoff = DateTime.UtcNow;
            CreateReplay(root, "before.replay", 1, cutoff.AddTicks(-1));
            CreateReplay(root, "after.replay", 2, cutoff.AddTicks(1));

            var manifest = await ReplayManifestService.FreezeAsync(root, cutoff);

            Assert.That(manifest.Entries.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "before.replay" }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Validate_DetectsContentMutationEvenWhenLengthAndTimestampAreRestored()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = CreateReplay(root, "match.replay", 1, DateTime.UtcNow);
            var manifest = await ReplayManifestService.FreezeAsync(root);
            var originalTime = File.GetLastWriteTimeUtc(path);
            await File.WriteAllBytesAsync(path, [2]);
            File.SetLastWriteTimeUtc(path, originalTime);

            var problems = await ReplayManifestService.ValidateAsync(manifest, root);

            Assert.That(problems, Has.Some.Contains("SHA-256 changed"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void CanonicalProjection_SortsOpponentsAndRetainsNullableFields()
    {
        var identity = new ReplayManifestEntry("match.replay", 1, DateTime.UtcNow, "ABC", 1, 1);
        var summary = new ReplaySummary
        {
            FileName = "match.replay",
            Kills = null,
            BotKills = null,
            Playlist = "Playlist_RopeSmileDuo",
            AnalysisStatus = ReplayAnalysisStatus.OwnerKillsUnavailable,
            OpponentAnalysisComplete = false,
            Opponents =
            [
                new OpponentSummary { StableId = "z", Name = "Zed" },
                new OpponentSummary { StableId = "a", Name = "Ada" }
            ]
        };

        var canonical = CanonicalReplaySummary.From(identity, summary);

        Assert.Multiple(() =>
        {
            Assert.That(canonical.Kills, Is.Null);
            Assert.That(canonical.BotKills, Is.Null);
            Assert.That(canonical.AnalysisStatus, Is.EqualTo(nameof(ReplayAnalysisStatus.OwnerKillsUnavailable)));
            Assert.That(canonical.OpponentAnalysisComplete, Is.False);
            Assert.That(canonical.Opponents.Select(opponent => opponent.StableId), Is.EqualTo(new[] { "a", "z" }));
        });
    }

    [Test]
    public void Compare_ReportsOnlyTheChangedPerFileField()
    {
        var baseline = Run("baseline", Canonical("match.replay", kills: 1));
        var candidate = Run("summary", Canonical("match.replay", kills: 2));

        var result = ReplayComparisonService.Compare(baseline, candidate);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsExactMatch, Is.False);
            Assert.That(result.Deltas, Has.Count.EqualTo(1));
            Assert.That(result.Deltas.Single().ChangedFields, Is.EqualTo(new[] { "Kills" }));
        });
    }

    private static ReplayOracleRun Run(string profile, CanonicalReplaySummary summary) => new(
        1, profile, DateTime.UtcNow, "manifest", OracleJson.SummaryFingerprint([summary]),
        new ReplayRunMetrics(0, 0, 0, 0, 1, 1, 0), [summary], []);

    private static CanonicalReplaySummary Canonical(string path, int? kills) => new(
        path, path, DateTime.UnixEpoch, "playlist", "mode", "", kills, null, 0, 0, 0, "",
        nameof(ReplayAnalysisStatus.Complete), true, []);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ReplayBenchmark.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateReplay(string directory, string name, byte value, DateTime timestamp)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [value]);
        File.SetLastWriteTimeUtc(path, timestamp);
        return path;
    }
}
