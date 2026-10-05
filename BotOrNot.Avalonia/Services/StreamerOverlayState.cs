using BotOrNot.Core.Models;

namespace BotOrNot.Avalonia.Services;

// Only broadcast fields belong here. Never serialize ReplaySummary (paths, owner and opponent identities).
public sealed record OverlayMatch(
    DateTime FileDate, string GameMode, string Region, string Placement,
    int? Kills, int? PlayerKills, int? BotKills, double BotPercent, string AnalysisStatus);

public sealed record OverlaySnapshot(
    DateTimeOffset UpdatedAt, int ScanLimit, int AvailableCount, int FailedCount,
    LibraryStatistics Statistics, IReadOnlyList<OverlayMatch> RecentMatches);

public sealed record OverlayPayload(
    bool HasDirectory, bool IsScanning, bool UpdatesDelayed,
    bool AutoRefreshEnabled, int AutoRefreshMinutes, OverlaySnapshot? Snapshot);

/// <summary>UI-thread writes; HTTP requests read one immutable payload without accessing UI collections.</summary>
public sealed class StreamerOverlayState
{
    private OverlayPayload _current = new(false, false, false, true, 10, null);
    public OverlayPayload Current => Volatile.Read(ref _current);

    public void Configure(bool hasDirectory, bool autoRefreshEnabled, int autoRefreshMinutes) =>
        Publish(Current with
        {
            HasDirectory = hasDirectory,
            AutoRefreshEnabled = autoRefreshEnabled,
            AutoRefreshMinutes = autoRefreshMinutes
        });

    public void Reset() => Publish(Current with { Snapshot = null, IsScanning = false, UpdatesDelayed = false });
    public void BeginScan() => Publish(Current with { IsScanning = true, UpdatesDelayed = false });
    public void FailScan() => Publish(Current with { IsScanning = false, UpdatesDelayed = true });

    public void CompleteScan(IReadOnlyCollection<ReplaySummary> replays, int scanLimit,
        int availableCount, int selectedCount, int failedCount, DateTimeOffset updatedAt)
    {
        // All selected files failing is a failed refresh, not evidence of an empty library.
        if (selectedCount > 0 && replays.Count == 0 && failedCount > 0)
        {
            FailScan();
            return;
        }

        var recent = replays.Take(3).Select(replay => new OverlayMatch(
            replay.FileDate, replay.GameMode, replay.MatchmakingRegionDisplay, replay.Placement,
            replay.Kills, replay.PlayerKills, replay.BotKills, replay.BotPercent,
            replay.AnalysisStatusText)).ToArray();
        Publish(Current with
        {
            IsScanning = false,
            UpdatesDelayed = false,
            Snapshot = new(updatedAt, scanLimit, availableCount, failedCount,
                LibraryStatistics.From(replays), Array.AsReadOnly(recent))
        });
    }

    private void Publish(OverlayPayload payload) => Volatile.Write(ref _current, payload);
}
