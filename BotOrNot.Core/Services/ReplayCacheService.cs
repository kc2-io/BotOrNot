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

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotOrNot");

    private static readonly string CachePath = Path.Combine(CacheDir, "replay-cache.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IReplayService _replayService;
    private readonly ILogger<ReplayCacheService> _logger;

    public ReplayCacheService(IReplayService? replayService = null, ILogger<ReplayCacheService>? logger = null)
    {
        _replayService = replayService ?? new ReplayService();
        _logger = logger ?? NullLogger<ReplayCacheService>.Instance;
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

            return new ReplaySummary
            {
                FileName = file.Name,
                FilePath = file.FullName,
                FileDate = file.LastWriteTimeUtc,
                GameMode = data.Metadata.GameMode,
                Playlist = data.Metadata.Playlist,
                Placement = ownerPlayer?.Placement ?? "",
                Kills = data.OwnerKills ?? ownerElims.Count,
                BotKills = botKills,
                PlayerCount = nonNpc.Count,
                BotCount = botCount,
                DurationMinutes = data.Metadata.MatchDurationMinutes,
                OwnerName = data.OwnerName ?? "",
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
        $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    private static Dictionary<string, ReplaySummary> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                return JsonSerializer.Deserialize<Dictionary<string, ReplaySummary>>(json, JsonOptions)
                    ?? new Dictionary<string, ReplaySummary>();
            }
        }
        catch { }
        return new Dictionary<string, ReplaySummary>();
    }

    private static void SaveCache(Dictionary<string, ReplaySummary> cache)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var json = JsonSerializer.Serialize(cache, JsonOptions);
            File.WriteAllText(CachePath, json);
        }
        catch { }
    }
}
