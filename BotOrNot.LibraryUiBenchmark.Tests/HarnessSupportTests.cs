using ReplayBenchmark;
using NUnit.Framework;

namespace BotOrNot.LibraryUiBenchmark.Tests;

[TestFixture]
public sealed class HarnessSupportTests
{
    [Test]
    public async Task FrozenManifest_RejectsChangedHashAndUnexpectedReplay()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var replay = Path.Combine(directory, "match.replay");
            await File.WriteAllBytesAsync(replay, [1, 2, 3, 4]);
            var manifest = await ReplayManifestService.FreezeAsync(directory);
            var manifestPath = Path.Combine(directory, "manifest.json");
            await OracleJson.WriteAsync(manifestPath, manifest);

            var valid = await BenchmarkManifest.LoadAndValidateAsync(manifestPath, directory);
            Assert.That(valid.Entries, Has.Count.EqualTo(1));

            await File.WriteAllBytesAsync(Path.Combine(directory, "unexpected.replay"), [9]);
            var changed = Assert.ThrowsAsync<InvalidOperationException>(
                () => BenchmarkManifest.LoadAndValidateAsync(manifestPath, directory));
            Assert.That(changed!.Message, Does.Contain("unexpected replay file"));

            File.Delete(Path.Combine(directory, "unexpected.replay"));
            await File.WriteAllBytesAsync(replay, [4, 3, 2, 1]);
            changed = Assert.ThrowsAsync<InvalidOperationException>(
                () => BenchmarkManifest.LoadAndValidateAsync(manifestPath, directory));
            Assert.That(changed!.Message, Does.Contain("SHA-256 changed"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void CacheMode_RequiresTheDeclaredPrecondition()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var cachePath = Path.Combine(directory, "replay-cache.json");
            File.WriteAllText(cachePath, "{}");
            Assert.Throws<InvalidOperationException>(() => BenchmarkCacheEvidence.Before(Arguments(cachePath, CacheMode.Cold)));

            File.Delete(cachePath);
            Assert.Throws<InvalidOperationException>(() => BenchmarkCacheEvidence.Before(Arguments(cachePath, CacheMode.Warm)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SelfTest_DoesNotClaimAnApplicationCacheState()
    {
        var arguments = BenchmarkArguments.Parse(["--self-test"]);
        Assert.That(arguments.CacheMode, Is.EqualTo(CacheMode.None));
    }

    private static BenchmarkArguments Arguments(string cachePath, CacheMode mode) => new(
        FixtureDirectory: "fixtures", CachePath: cachePath, ManifestPath: "manifest.json",
        Concurrency: 2, Limit: 79, Mode: ScanMode.Auto, OutputPath: "result.json", RenderPngPath: null,
        Width: 1000, Height: 700, Timeout: TimeSpan.FromSeconds(10), SelfTest: false,
        Profile: "summary", CacheMode: mode);

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BotOrNot.LibraryUiBenchmark.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
