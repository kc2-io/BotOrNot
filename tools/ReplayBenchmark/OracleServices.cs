using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace ReplayBenchmark;

public static class ReplayManifestService
{
    public static async Task<ReplayManifest> FreezeAsync(
        string replayRoot,
        DateTime? modifiedBeforeUtc = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(replayRoot);
        var files = Directory.EnumerateFiles(root, "*.replay", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => !modifiedBeforeUtc.HasValue || file.LastWriteTimeUtc < modifiedBeforeUtc.Value)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FullName, StringComparer.Ordinal)
            .ToArray();

        var entries = new List<ReplayManifestEntry>(files.Length);
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            await using var stream = File.OpenRead(file.FullName);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            entries.Add(new ReplayManifestEntry(
                Path.GetRelativePath(root, file.FullName), file.Length, file.LastWriteTimeUtc, digest,
                index + 1, index < 50 ? index + 1 : null));
        }

        return new ReplayManifest(ReplayManifest.CurrentSchemaVersion, DateTime.UtcNow, Path.GetFileName(root), entries);
    }

    public static async Task<IReadOnlyList<string>> ValidateAsync(
        ReplayManifest manifest, string replayRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(replayRoot);
        var problems = new List<string>();
        foreach (var entry in manifest.Entries.OrderBy(entry => entry.AllRank))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{entry.RelativePath}: escapes replay root");
                continue;
            }
            if (!File.Exists(path))
            {
                problems.Add($"{entry.RelativePath}: missing");
                continue;
            }

            var file = new FileInfo(path);
            if (file.Length != entry.Length)
                problems.Add($"{entry.RelativePath}: length expected {entry.Length}, got {file.Length}");
            if (file.LastWriteTimeUtc != entry.LastWriteTimeUtc)
                problems.Add($"{entry.RelativePath}: last-write time changed");

            await using var stream = File.OpenRead(path);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(digest, entry.Sha256, StringComparison.Ordinal))
                problems.Add($"{entry.RelativePath}: SHA-256 changed");
        }
        return problems;
    }

    public static string Fingerprint(ReplayManifest manifest) => OracleJson.Fingerprint(manifest.Entries
        .OrderBy(entry => entry.AllRank)
        .ToArray());
}

public static class ReplayOracleService
{
    public static async Task<ReplayOracleRun> RunAsync(
        ReplayManifest manifest,
        string replayRoot,
        string profile,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        if (concurrency is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(concurrency), "Concurrency must be between 1 and 4.");

        var validation = await ReplayManifestService.ValidateAsync(manifest, replayRoot, cancellationToken).ConfigureAwait(false);
        if (validation.Count > 0)
            throw new InvalidOperationException("Frozen replay input changed:" + Environment.NewLine + string.Join(Environment.NewLine, validation));

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        var summaries = new ConcurrentBag<CanonicalReplaySummary>();
        var failures = new ConcurrentBag<ReplayRunFailure>();

        await Parallel.ForEachAsync(manifest.Entries,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (entry, token) =>
            {
                try
                {
                    var path = Path.Combine(replayRoot, entry.RelativePath);
                    var summary = await LoadAsync(profile, path, token).ConfigureAwait(false);
                    summaries.Add(CanonicalReplaySummary.From(entry, summary));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add(new ReplayRunFailure(entry.RelativePath, exception.GetType().FullName ?? exception.GetType().Name,
                        exception.Message));
                }
            }).ConfigureAwait(false);

        stopwatch.Stop();
        process.Refresh();
        var orderedSummaries = summaries
            .OrderBy(summary => summary.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var orderedFailures = failures.OrderBy(failure => failure.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(failure => failure.RelativePath, StringComparer.Ordinal).ToArray();
        return new ReplayOracleRun(
            ReplayOracleRun.CurrentSchemaVersion,
            profile,
            DateTime.UtcNow,
            ReplayManifestService.Fingerprint(manifest),
            OracleJson.SummaryFingerprint(orderedSummaries),
            new ReplayRunMetrics(
                stopwatch.Elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocationStart,
                process.PeakWorkingSet64,
                concurrency,
                orderedSummaries.Length,
                orderedFailures.Length),
            orderedSummaries,
            orderedFailures);
    }

    private static async Task<ReplaySummary> LoadAsync(string profile, string path, CancellationToken cancellationToken)
    {
        if (string.Equals(profile, "normal", StringComparison.OrdinalIgnoreCase))
        {
            var replay = await new ReplayService().LoadReplayAsync(path, cancellationToken).ConfigureAwait(false);
            return ReplaySummaryFactory.Create(replay, new FileInfo(path));
        }

        if (!string.Equals(profile, "summary", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Profile must be 'normal' or 'summary'.", nameof(profile));

        // The summary API is introduced by the parser work. Reflection keeps this tool buildable
        // against the current main branch while failing loudly until that public API is available.
        var service = new ReplayService();
        var method = service.GetType().GetMethod("LoadSummaryAsync", BindingFlags.Instance | BindingFlags.Public,
            binder: null, types: [typeof(string), typeof(CancellationToken)], modifiers: null);
        if (method is null)
            throw new NotSupportedException(
                "ReplayService.LoadSummaryAsync(string, CancellationToken) is not available in this build.");
        var task = method.Invoke(service, [path, cancellationToken]) as Task
            ?? throw new InvalidOperationException("LoadSummaryAsync did not return a Task.");
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        return result as ReplaySummary
            ?? throw new InvalidOperationException("LoadSummaryAsync did not return ReplaySummary.");
    }
}

public static class ReplayComparisonService
{
    public static ReplayOracleComparison Compare(ReplayOracleRun expected, ReplayOracleRun actual)
    {
        var expectedByPath = expected.Summaries.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var actualByPath = actual.Summaries.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var paths = expectedByPath.Keys.Concat(actualByPath.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal);
        var deltas = new List<ReplaySummaryDelta>();
        foreach (var path in paths)
        {
            var hasExpected = expectedByPath.TryGetValue(path, out var expectedSummary);
            var hasActual = actualByPath.TryGetValue(path, out var actualSummary);
            if (!hasExpected || !hasActual)
            {
                deltas.Add(new ReplaySummaryDelta(path, hasExpected ? "missing-actual" : "unexpected-actual",
                    hasExpected ? OracleJson.Fingerprint(expectedSummary) : null,
                    hasActual ? OracleJson.Fingerprint(actualSummary) : null, []));
                continue;
            }
            var changed = ChangedFields(expectedSummary!, actualSummary!);
            if (changed.Count > 0)
                deltas.Add(new ReplaySummaryDelta(path, "value-mismatch", OracleJson.Fingerprint(expectedSummary),
                    OracleJson.Fingerprint(actualSummary), changed));
        }

        foreach (var failure in expected.Failures)
            deltas.Add(new ReplaySummaryDelta(failure.RelativePath, "expected-failure", null, null, [failure.Message]));
        foreach (var failure in actual.Failures)
            deltas.Add(new ReplaySummaryDelta(failure.RelativePath, "actual-failure", null, null, [failure.Message]));

        return new ReplayOracleComparison(
            1,
            DateTime.UtcNow,
            expected.ManifestFingerprint,
            actual.ManifestFingerprint,
            expected.SummaryFingerprint,
            actual.SummaryFingerprint,
            string.Equals(expected.ManifestFingerprint, actual.ManifestFingerprint, StringComparison.Ordinal) &&
            string.Equals(expected.SummaryFingerprint, actual.SummaryFingerprint, StringComparison.Ordinal) &&
            deltas.Count == 0,
            deltas);
    }

    private static IReadOnlyList<string> ChangedFields(CanonicalReplaySummary expected, CanonicalReplaySummary actual)
    {
        var names = new List<string>();
        if (expected.FileName != actual.FileName) names.Add(nameof(expected.FileName));
        if (expected.FileDateUtc != actual.FileDateUtc) names.Add(nameof(expected.FileDateUtc));
        if (expected.Playlist != actual.Playlist) names.Add(nameof(expected.Playlist));
        if (expected.DisplayGameMode != actual.DisplayGameMode) names.Add(nameof(expected.DisplayGameMode));
        if (expected.Placement != actual.Placement) names.Add(nameof(expected.Placement));
        if (expected.Kills != actual.Kills) names.Add(nameof(expected.Kills));
        if (expected.BotKills != actual.BotKills) names.Add(nameof(expected.BotKills));
        if (expected.PlayerCount != actual.PlayerCount) names.Add(nameof(expected.PlayerCount));
        if (expected.BotCount != actual.BotCount) names.Add(nameof(expected.BotCount));
        if (expected.DurationMinutes != actual.DurationMinutes) names.Add(nameof(expected.DurationMinutes));
        if (expected.OwnerName != actual.OwnerName) names.Add(nameof(expected.OwnerName));
        if (expected.AnalysisStatus != actual.AnalysisStatus) names.Add(nameof(expected.AnalysisStatus));
        if (expected.OpponentAnalysisComplete != actual.OpponentAnalysisComplete) names.Add(nameof(expected.OpponentAnalysisComplete));
        if (!expected.Opponents.SequenceEqual(actual.Opponents)) names.Add(nameof(expected.Opponents));
        return names;
    }
}
