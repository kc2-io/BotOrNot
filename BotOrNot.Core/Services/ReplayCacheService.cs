using System.Text.Json;
using BotOrNot.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotOrNot.Core.Services;

public interface IReplayCacheService
{
    Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class ReplayCacheService : IReplayCacheService
{
    private const int MaxReplays = 50;
    /// <summary>
    /// Bump when a parser or summary interpretation changes. It is deliberately part of the
    /// key so prior cache entries cannot masquerade as current analysis.
    /// </summary>
    public const string AnalysisRevision = "2026-09-10.1";

    private static readonly string DefaultCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotOrNot");

    private static readonly string DefaultCachePath = Path.Combine(DefaultCacheDir, "replay-cache.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IReplayService _replayService;
    private readonly ILogger<ReplayCacheService> _logger;
    private readonly string _cachePath;

    public ReplayCacheService(
        IReplayService? replayService = null,
        ILogger<ReplayCacheService>? logger = null,
        string? cachePath = null)
    {
        _replayService = replayService ?? new ReplayService();
        _logger = logger ?? NullLogger<ReplayCacheService>.Instance;
        _cachePath = cachePath ?? DefaultCachePath;
    }

    public async Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
        string directory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = Directory.GetFiles(directory, "*.replay")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxReplays)
            .ToList();

        var cache = LoadCache();
        var results = new List<ReplaySummary>(files.Count);
        int done = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = CacheKey(file);
            if (cache.TryGetValue(key, out var cached))
            {
                results.Add(cached);
            }
            else
            {
                var summary = await ParseFileAsync(file, cancellationToken);
                if (summary != null)
                {
                    cache[key] = summary;
                    results.Add(summary);
                }
            }

            done++;
            progress?.Report((int)((double)done / files.Count * 100));
        }

        SaveCache(cache);
        return results;
    }

    private async Task<ReplaySummary?> ParseFileAsync(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _replayService.LoadReplayAsync(file.FullName, cancellationToken);
            var nonNpc = data.Players.Where(p => !p.IsNpc).ToList();
            var botCount = nonNpc.Count(p => p.IsBot);
            var ownerElims = data.OwnerEliminations.Where(p => !p.IsNpc).ToList();
            var botKills = ownerElims.Count(p => p.IsBot);
            var ownerPlayer = data.Players.FirstOrDefault(p =>
                !string.IsNullOrEmpty(data.OwnerName) &&
                p.Name?.Equals(data.OwnerName, StringComparison.OrdinalIgnoreCase) == true);

            var analysisStatus = string.IsNullOrWhiteSpace(data.OwnerName)
                ? ReplayAnalysisStatus.OwnerIdentityUnavailable
                : data.OwnerKills.HasValue
                    ? ReplayAnalysisStatus.Complete
                    : ReplayAnalysisStatus.OwnerKillsUnavailable;

            return new ReplaySummary
            {
                FileName = file.Name,
                FilePath = file.FullName,
                FileDate = file.LastWriteTimeUtc,
                GameMode = data.Metadata.GameMode,
                Playlist = data.Metadata.Playlist,
                Placement = ownerPlayer?.Placement ?? "",
                Kills = analysisStatus == ReplayAnalysisStatus.Complete ? data.OwnerKills : null,
                BotKills = analysisStatus == ReplayAnalysisStatus.Complete ? botKills : null,
                PlayerCount = nonNpc.Count,
                BotCount = botCount,
                DurationMinutes = data.Metadata.RecordingDurationMinutes,
                OwnerName = data.OwnerName ?? "",
                AnalysisStatus = analysisStatus,
                PlayerNames = nonNpc.Where(p => !p.IsBot)
                    .Select(p => p.Name ?? "")
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse replay {File}", file.Name);
            return null;
        }
    }

    private static string CacheKey(FileInfo file) =>
        $"{AnalysisRevision}|{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    private Dictionary<string, ReplaySummary> LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                var cache = JsonSerializer.Deserialize<Dictionary<string, ReplaySummary>>(json, JsonOptions)
                    ?? new Dictionary<string, ReplaySummary>();
                var currentPrefix = $"{AnalysisRevision}|";
                return cache
                    .Where(entry => entry.Key.StartsWith(currentPrefix, StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            }
        }
        catch { }
        return new Dictionary<string, ReplaySummary>();
    }

    private void SaveCache(Dictionary<string, ReplaySummary> cache)
    {
        try
        {
            var cacheDirectory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);
            var json = JsonSerializer.Serialize(cache, JsonOptions);
            File.WriteAllText(_cachePath, json);
        }
        catch { }
    }
}
