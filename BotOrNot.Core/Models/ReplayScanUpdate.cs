namespace BotOrNot.Core.Models;

public sealed record ReplayScanOptions
{
    public const int DefaultLimit = 50;
    public const int DefaultMaxConcurrency = 2;

    public int Limit { get; init; } = DefaultLimit;
    public int MaxConcurrency { get; init; } = DefaultMaxConcurrency;
}

public sealed record ReplayFileIdentity(
    string FileName,
    string FullPath,
    long Length,
    DateTime LastWriteTimeUtc);

public enum ReplayScanStatus
{
    Started,
    Cached,
    Loaded,
    Failed,
    Completed
}

public sealed record ReplayScanUpdate
{
    public required Guid ScanId { get; init; }
    public required ReplayScanStatus Status { get; init; }
    public required int ConfiguredLimit { get; init; }
    public required int AvailableCount { get; init; }
    public required int SelectedCount { get; init; }
    public required int ProcessedCount { get; init; }
    public required int LoadedCount { get; init; }
    public required int FailedCount { get; init; }
    public ReplayFileIdentity? File { get; init; }
    public ReplaySummary? Summary { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsComplete => Status == ReplayScanStatus.Completed;
    public int ProgressPercentage => SelectedCount == 0
        ? 100
        : (int)((long)ProcessedCount * 100 / SelectedCount);
}
