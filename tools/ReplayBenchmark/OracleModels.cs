using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotOrNot.Core.Models;

namespace ReplayBenchmark;

public sealed record ReplayManifest(
    int SchemaVersion,
    DateTime CreatedUtc,
    string RootHint,
    IReadOnlyList<ReplayManifestEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ReplayManifestEntry(
    string RelativePath,
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256,
    int AllRank,
    int? Newest50Rank);

public sealed record CanonicalOpponent(string StableId, string Name);

/// <summary>
/// Stable, deliberately complete cache/UI summary projection. Paths are manifest-relative so an
/// oracle can be moved to another machine without changing the output fingerprint.
/// </summary>
public sealed record CanonicalReplaySummary(
    string RelativePath,
    string FileName,
    DateTime FileDateUtc,
    string Playlist,
    string DisplayGameMode,
    string Placement,
    int? Kills,
    int? BotKills,
    int PlayerCount,
    int BotCount,
    double DurationMinutes,
    string OwnerName,
    string AnalysisStatus,
    string AnalysisStatusText,
    bool OpponentAnalysisComplete,
    int? PlayerKills,
    bool IsWin,
    double BotPercent,
    IReadOnlyList<CanonicalOpponent> Opponents)
{
    public static CanonicalReplaySummary From(
        ReplayManifestEntry identity,
        ReplaySummary summary,
        string inputPath)
    {
        var expectedPath = Path.GetFullPath(inputPath);
        var actualPath = Path.GetFullPath(summary.FilePath);
        if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Summary FilePath '{summary.FilePath}' does not resolve to decoded input '{expectedPath}'.");

        return new(
        identity.RelativePath,
        summary.FileName,
        summary.FileDate.ToUniversalTime(),
        summary.Playlist,
        summary.GameMode,
        summary.Placement,
        summary.Kills,
        summary.BotKills,
        summary.PlayerCount,
        summary.BotCount,
        summary.DurationMinutes,
        summary.OwnerName,
        summary.AnalysisStatus.ToString(),
        summary.AnalysisStatusText,
        summary.OpponentAnalysisComplete,
        summary.PlayerKills,
        summary.IsWin,
        summary.BotPercent,
        summary.Opponents
            .Select(opponent => new CanonicalOpponent(opponent.StableId, opponent.Name))
            .OrderBy(opponent => opponent.StableId, StringComparer.Ordinal)
            .ThenBy(opponent => opponent.Name, StringComparer.Ordinal)
            .ToArray());
    }
}

public sealed record ReplayRunFailure(string RelativePath, string ErrorType, string Message);

public sealed record ReplayRunMetrics(
    double WallMilliseconds,
    double CpuMilliseconds,
    long AllocatedBytes,
    long PeakWorkingSetBytes,
    int RequestedConcurrency,
    int SuccessCount,
    int FailureCount);

public sealed record ReplayEnvironment(
    string FrameworkDescription,
    string RuntimeVersion,
    string OperatingSystemDescription,
    string ProcessArchitecture,
    int ProcessorCount,
    string CoreAssemblyVersion,
    string CoreAssemblyInformationalVersion,
    string ParserAssemblyVersion,
    string ParserAssemblyInformationalVersion,
    string ParserAssemblySha256);

public sealed record DiagnosticMetric(double Milliseconds, long AllocatedBytes);

/// <summary>Inclusive measurements. Nested parser stages intentionally overlap.</summary>
public sealed record ReplayDiagnosticStage(
    string Name,
    long Calls,
    double InclusiveMilliseconds,
    long InclusiveAllocatedBytes);

public sealed record ReplayDiagnosticRun(
    int SchemaVersion,
    DateTime CreatedUtc,
    string FileName,
    long FileLength,
    ReplayEnvironment Environment,
    DiagnosticMetric Constructor,
    DiagnosticMetric ReadReplay,
    IReadOnlyList<ReplayDiagnosticStage> Stages)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ReplayOracleRun(
    int SchemaVersion,
    string Profile,
    DateTime CreatedUtc,
    string ManifestFingerprint,
    string SummaryFingerprint,
    ReplayEnvironment Environment,
    ReplayRunMetrics Metrics,
    IReadOnlyList<CanonicalReplaySummary> Summaries,
    IReadOnlyList<ReplayRunFailure> Failures)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ReplaySummaryDelta(
    string RelativePath,
    string Kind,
    string? ExpectedFingerprint,
    string? ActualFingerprint,
    IReadOnlyList<string> ChangedFields);

public sealed record ReplayOracleComparison(
    int SchemaVersion,
    DateTime CreatedUtc,
    string ExpectedManifestFingerprint,
    string ActualManifestFingerprint,
    string ExpectedSummaryFingerprint,
    string ActualSummaryFingerprint,
    bool IsExactMatch,
    IReadOnlyList<ReplaySummaryDelta> Deltas);

public static class OracleJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Fingerprint<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value,
            new JsonSerializerOptions(Options) { WriteIndented = false });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string SummaryFingerprint(IEnumerable<CanonicalReplaySummary> summaries) =>
        Fingerprint(summaries
            .OrderBy(summary => summary.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.RelativePath, StringComparer.Ordinal)
            .ToArray());

    public static async Task WriteAsync<T>(string outputPath, T value, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Output path must have a directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath,
                JsonSerializer.Serialize(value, Options), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static async Task<T> ReadAsync<T>(string inputPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(inputPath));
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Could not deserialize {inputPath}.");
    }
}
