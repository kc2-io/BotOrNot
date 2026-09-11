using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BotOrNot.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotOrNot.Core.Services;

public interface IReplayCacheService
{
    Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
        string directory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    // Keeps legacy test doubles source-compatible. The concrete service below supplies the
    // genuinely streaming implementation; this fallback adapts final legacy collections.
    async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
        string directory,
        ReplayScanOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scanOptions = options ?? new ReplayScanOptions();
        if (scanOptions.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Replay scan limit must be positive.");
        if (scanOptions.MaxConcurrency is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(options), "Replay scan concurrency must be between 1 and 4.");
        var scanId = Guid.NewGuid();
        var available = await GetSummariesAsync(directory, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var summaries = available.OrderByDescending(summary => summary.FileDate)
            .ThenBy(summary => Path.GetFullPath(summary.FilePath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => Path.GetFullPath(summary.FilePath), StringComparer.Ordinal)
            .Take(scanOptions.Limit)
            .ToList();
        yield return MakeLegacyUpdate(ReplayScanStatus.Started, scanId, scanOptions.Limit,
            available.Count, summaries.Count, 0, null);

        var processed = 0;
        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            yield return MakeLegacyUpdate(ReplayScanStatus.Loaded, scanId, scanOptions.Limit,
                available.Count, summaries.Count, processed, summary);
        }

        yield return MakeLegacyUpdate(ReplayScanStatus.Completed, scanId, scanOptions.Limit,
            available.Count, summaries.Count, processed, null);
    }

    private static ReplayScanUpdate MakeLegacyUpdate(
        ReplayScanStatus status,
        Guid scanId,
        int limit,
        int availableCount,
        int selectedCount,
        int processed,
        ReplaySummary? summary) => new()
        {
            ScanId = scanId,
            Status = status,
            ConfiguredLimit = limit,
            AvailableCount = availableCount,
            SelectedCount = selectedCount,
            ProcessedCount = processed,
            LoadedCount = processed,
            FailedCount = 0,
            Summary = summary,
            File = summary is null ? null : new ReplayFileIdentity(
            summary.FileName, summary.FilePath, 0, summary.FileDate)
        };
}

public sealed class ReplayCacheService : IReplayCacheService
{
    /// <summary>
    /// Bump when a parser or summary interpretation changes. It is deliberately part of the
    /// key so prior cache entries cannot masquerade as current analysis.
    /// </summary>
    public const string AnalysisRevision = "2026-09-11.4";

    private static readonly string DefaultCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotOrNot");
    private static readonly string DefaultCachePath = Path.Combine(DefaultCacheDir, "replay-cache.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheWriters =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IReplayService _replayService;
    private readonly ILogger<ReplayCacheService> _logger;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _cacheWriter;

    public ReplayCacheService(
        IReplayService? replayService = null,
        ILogger<ReplayCacheService>? logger = null,
        string? cachePath = null)
    {
        _replayService = replayService ?? new ReplayService();
        _logger = logger ?? NullLogger<ReplayCacheService>.Instance;
        _cachePath = cachePath ?? DefaultCachePath;
        _cacheWriter = CacheWriters.GetOrAdd(NormalizePath(_cachePath), _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
        string directory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var summaries = new List<ReplaySummary>();
        await foreach (var update in ScanAsync(directory, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            if (update.Status is ReplayScanStatus.Cached or ReplayScanStatus.Loaded)
                summaries.Add(update.Summary!);
            if (update.Status is ReplayScanStatus.Cached or ReplayScanStatus.Loaded or ReplayScanStatus.Failed)
                progress?.Report(update.ProgressPercentage);
        }

        return summaries
            .OrderByDescending(summary => summary.FileDate)
            .ThenBy(summary => NormalizePath(summary.FilePath), PathComparer)
            .ThenBy(summary => NormalizePath(summary.FilePath), StringComparer.Ordinal)
            .ToList();
    }

    public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
        string directory,
        ReplayScanOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scanOptions = Validate(options ?? new ReplayScanOptions());
        var scanId = Guid.NewGuid();
        // Freeze both the inventory and the selected newest subset for the life of this scan.
        var allFiles = Directory.EnumerateFiles(directory, "*.replay", SearchOption.TopDirectoryOnly)
            .Select(CreateIdentity)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullPath, PathComparer)
            .ThenBy(file => file.FullPath, StringComparer.Ordinal)
            .ToList();
        var selectedFiles = allFiles.Take(Math.Min(scanOptions.Limit, allFiles.Count)).ToList();
        var processed = 0;
        var loaded = 0;
        var failed = 0;

        ReplayScanUpdate Update(
            ReplayScanStatus status,
            ReplayFileIdentity? file = null,
            ReplaySummary? summary = null,
            string? errorMessage = null) => new()
            {
                ScanId = scanId,
                Status = status,
                ConfiguredLimit = scanOptions.Limit,
                AvailableCount = allFiles.Count,
                SelectedCount = selectedFiles.Count,
                ProcessedCount = processed,
                LoadedCount = loaded,
                FailedCount = failed,
                File = file,
                Summary = summary,
                ErrorMessage = errorMessage
            };

        // The UI gets exact denominators before any cache read or replay decode.
        yield return Update(ReplayScanStatus.Started);

        var cache = await LoadCacheSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var uncachedFiles = new List<ReplayFileIdentity>();
        foreach (var file in selectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cache.TryGetValue(CacheKey(file), out var summary))
            {
                processed++;
                loaded++;
                yield return Update(ReplayScanStatus.Cached, file, summary);
            }
            else
            {
                uncachedFiles.Add(file);
            }
        }

        if (uncachedFiles.Count > 0)
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var scanToken = scanCts.Token;
            var outcomes = Channel.CreateBounded<ParseOutcome>(new BoundedChannelOptions(scanOptions.MaxConcurrency)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            var work = Channel.CreateBounded<ReplayFileIdentity>(new BoundedChannelOptions(scanOptions.MaxConcurrency)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

            var workers = Enumerable.Range(0, Math.Min(scanOptions.MaxConcurrency, uncachedFiles.Count))
                .Select(_ => ParseWorkerAsync(work.Reader, outcomes.Writer, scanToken))
                .ToArray();
            var producer = ProduceWorkAsync(uncachedFiles, work.Writer, scanToken);
            var completion = CompleteOutcomesAsync(workers, outcomes.Writer);

            try
            {
                await foreach (var outcome in outcomes.Reader.ReadAllAsync(scanToken).ConfigureAwait(false))
                {
                    processed++;
                    if (outcome.Summary is not null)
                    {
                        loaded++;
                        yield return Update(ReplayScanStatus.Loaded, outcome.File, outcome.Summary);
                    }
                    else
                    {
                        failed++;
                        yield return Update(ReplayScanStatus.Failed, outcome.File,
                            errorMessage: outcome.ErrorMessage);
                    }
                }
            }
            finally
            {
                // Async-iterator disposal is also cancellation: stop the producer and workers
                // even when the caller breaks without cancelling its supplied token.
                scanCts.Cancel();
                work.Writer.TryComplete();
                try
                {
                    await Task.WhenAll(producer, completion).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (scanToken.IsCancellationRequested) { }
                catch (ChannelClosedException) when (scanToken.IsCancellationRequested) { }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return Update(ReplayScanStatus.Completed);
    }

    private async Task ParseWorkerAsync(
        ChannelReader<ReplayFileIdentity> work,
        ChannelWriter<ParseOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        await foreach (var file in work.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var outcome = await ParseFileAsync(file, cancellationToken).ConfigureAwait(false);
            if (outcome.Summary is not null)
            {
                // A completed decode is durable even if the consumer cancels before observing it.
                await UpsertCacheEntryAsync(CacheKey(outcome.File), outcome.Summary, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            await outcomes.WriteAsync(outcome, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ProduceWorkAsync(
        IReadOnlyList<ReplayFileIdentity> files,
        ChannelWriter<ReplayFileIdentity> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in files)
                await writer.WriteAsync(file, cancellationToken).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    private static async Task CompleteOutcomesAsync(Task[] workers, ChannelWriter<ParseOutcome> writer)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    private async Task<ParseOutcome> ParseFileAsync(
        ReplayFileIdentity file,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _replayService.LoadReplayAsync(file.FullPath, cancellationToken)
                .ConfigureAwait(false);
            var currentIdentity = TryCreateIdentity(file.FullPath);
            if (currentIdentity is null || currentIdentity.Length != file.Length ||
                currentIdentity.LastWriteTimeUtc != file.LastWriteTimeUtc)
            {
                return ParseOutcome.Failure(file, "Replay file changed while it was being analyzed.");
            }

            return ParseOutcome.Success(file, ReplaySummaryFactory.Create(data, new FileInfo(file.FullPath)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse replay {File}", file.FileName);
            return ParseOutcome.Failure(file, ex.Message);
        }
    }

    private async Task<Dictionary<string, ReplaySummary>> LoadCacheSnapshotAsync(
        CancellationToken cancellationToken)
    {
        await _cacheWriter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return LoadCache(); }
        finally { _cacheWriter.Release(); }
    }

    private async Task UpsertCacheEntryAsync(
        string key,
        ReplaySummary summary,
        CancellationToken cancellationToken)
    {
        await _cacheWriter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = LoadCache();
            cache[key] = summary;
            await SaveCacheAtomicallyAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally { _cacheWriter.Release(); }
    }

    private Dictionary<string, ReplaySummary> LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                var cache = JsonSerializer.Deserialize<Dictionary<string, ReplaySummary>>(json, JsonOptions)
                    ?? new Dictionary<string, ReplaySummary>();
                var prefix = $"{AnalysisRevision}|";
                return cache.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load replay cache {CachePath}", _cachePath);
        }
        return new Dictionary<string, ReplaySummary>(StringComparer.Ordinal);
    }

    private async Task SaveCacheAtomicallyAsync(
        Dictionary<string, ReplaySummary> cache,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.GetDirectoryName(_cachePath);
        var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!string.IsNullOrWhiteSpace(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);
            var json = JsonSerializer.Serialize(cache, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save replay cache {CachePath}", _cachePath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch { }
        }
    }

    private static ReplayScanOptions Validate(ReplayScanOptions options)
    {
        if (options.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Replay scan limit must be positive.");
        if (options.MaxConcurrency is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(options), "Replay scan concurrency must be between 1 and 4.");
        return options;
    }

    private static ReplayFileIdentity CreateIdentity(string path)
    {
        var file = new FileInfo(path);
        return new ReplayFileIdentity(file.Name, NormalizePath(file.FullName),
            file.Length, file.LastWriteTimeUtc);
    }

    private static ReplayFileIdentity? TryCreateIdentity(string path)
    {
        try { return File.Exists(path) ? CreateIdentity(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string CacheKey(ReplayFileIdentity file)
    {
        var pathBytes = Encoding.UTF8.GetBytes(file.FullPath.ToUpperInvariant());
        return $"{AnalysisRevision}|{Convert.ToBase64String(pathBytes)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private sealed record ParseOutcome(
        ReplayFileIdentity File,
        ReplaySummary? Summary,
        string? ErrorMessage)
    {
        public static ParseOutcome Success(ReplayFileIdentity file, ReplaySummary summary) =>
            new(file, summary, null);
        public static ParseOutcome Failure(ReplayFileIdentity file, string errorMessage) =>
            new(file, null, errorMessage);
    }
}
