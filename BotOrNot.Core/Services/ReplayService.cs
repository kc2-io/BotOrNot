using BotOrNot.Core.Models;
using FortniteReplayReader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unreal.Core.Models.Enums;

namespace BotOrNot.Core.Services;

public interface IReplayService
{
    Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class ReplayService : IReplayService
{
    private readonly ILogger<ReplayService> _logger;
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(90);

    // ReplayReader is synchronous and offers no cooperative cancellation. Keep a physical
    // decoder lease until ReadReplay really exits, even when the caller stops waiting. This
    // bounds timed-out/cancelled parser threads across overlapping scans instead of treating
    // an abandoned await as available decoder capacity.
    private static readonly SemaphoreSlim PhysicalDecoderSlots = new(4, 4);

    // Reuse a single no-op logger for the replay reader instead of creating a LoggerFactory per call
    private static readonly ILogger<ReplayReader> ReaderLogger = NullLoggerFactory.Instance.CreateLogger<ReplayReader>();

    public ReplayService(ILogger<ReplayService>? logger = null)
    {
        _logger = logger ?? NullLogger<ReplayService>.Instance;
    }

    public async Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
    {
        await PhysicalDecoderSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<FortniteReplayReader.Models.FortniteReplay> replayTask;
        try
        {
            var reader = new ReplayReader(ReaderLogger, ParseMode.Normal);
            replayTask = Task.Run(() => reader.ReadReplay(path), CancellationToken.None);
        }
        catch
        {
            PhysicalDecoderSlots.Release();
            throw;
        }
        _ = replayTask.ContinueWith(
            completedTask =>
            {
                // Observe a late fault after timeout/cancellation and release only when the
                // synchronous decoder has physically stopped using CPU and parser state.
                _ = completedTask.Exception;
                PhysicalDecoderSlots.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var timeoutTask = Task.Delay(ParseTimeout, CancellationToken.None);
        var cancelledTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(replayTask, timeoutTask, cancelledTask).ConfigureAwait(false);
        if (completed != replayTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                "Replay parsing timed out after 90 seconds. " +
                "This replay may be from a version of Fortnite not yet supported by the parser.");
        }

        var result = await replayTask.ConfigureAwait(false);

        // Safe-zone observations use the replay frame clock. Elimination EventInfo.StartTime
        // uses the same clock, unlike the replicated world clock and formatted Time property.
        var stormObservations = new List<StormCircleObservation>();
        var safeZonesObj = ReflectionUtils.GetObject(result.MapData, "SafeZones");
        if (safeZonesObj is System.Collections.IEnumerable safeZonesEnum)
        {
            foreach (var zone in safeZonesEnum)
            {
                var replayTime = ReflectionUtils.GetDouble(zone, "ReplayTimeSeconds");
                if (!replayTime.HasValue)
                    continue;

                var channel = ReflectionUtils.GetUInt(zone, "ChannelIndex");
                var actorGuid = ReflectionUtils.GetUInt(zone, "ActorGuid");
                stormObservations.Add(new StormCircleObservation(
                    replayTime.Value,
                    ReflectionUtils.GetInt(zone, "CurrentPhase"),
                    ReflectionUtils.GetInt(zone, "PhaseCount"),
                    channel,
                    actorGuid));
            }
        }
        var stormCircleResolver = new StormCircleResolver(stormObservations);

        // Pre-size dictionaries for typical Fortnite lobby (~100 players)
        var playersById = new Dictionary<string, PlayerRow>(128, StringComparer.OrdinalIgnoreCase);
        var playersByNumericId = new Dictionary<string, PlayerRow>(128, StringComparer.OrdinalIgnoreCase);

        // Owner detection — the parser's IsReplayOwner flag is the only authority.

        // === PASS 1: Single iteration over PlayerData ===
        // Extracts player attributes, builds numericId lookup, and detects replay owner
        foreach (var pd in result.PlayerData ?? Enumerable.Empty<object>())
        {
            var id = ReflectionUtils.FirstString(pd, "PlayerId", "UniqueId", "NetId");
            var name = ReflectionUtils.FirstString(pd, "PlayerName", "DisplayName", "Name");
            var level = ReflectionUtils.FirstString(pd, "Level");
            var bot = ReflectionUtils.FirstString(pd, "IsBot");
            var platform = ReflectionUtils.FirstString(pd, "Platform");
            var kills = ReflectionUtils.FirstString(pd, "Kills");
            var teamIndex = ReflectionUtils.FirstString(pd, "TeamIndex");
            var stableId = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
            var teamIndexValue = ParticipantClassifier.NormalizeTeamIndex(teamIndex);
            var death = ReflectionUtils.FirstString(pd, "DeathCause");
            var deathTagsObj = ReflectionUtils.GetObject(pd, "DeathTags");
            var deathTagStrings = (deathTagsObj as System.Collections.IEnumerable)?
                .Cast<object>()
                .Select(t => t.ToString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            var placement = ReflectionUtils.FirstString(pd, "Placement");

            var cosmetics = ReflectionUtils.GetObject(pd, "Cosmetics");
            var pickaxe = ReflectionUtils.FirstString(cosmetics, "Pickaxe") ?? "unknown";
            var glider = ReflectionUtils.FirstString(cosmetics, "Glider") ?? "unknown";

            var key = !string.IsNullOrWhiteSpace(id) ? id
                : !string.IsNullOrWhiteSpace(name) ? name
                : Guid.NewGuid().ToString("N");

            if (!playersById.TryGetValue(key, out var row))
            {
                row = new PlayerRow { Id = key };
                playersById[key] = row;
            }

            row.StableId ??= stableId;
            row.Name = string.IsNullOrWhiteSpace(name) ? (row.Name ?? "unknown") : name;
            row.Level = string.IsNullOrWhiteSpace(level) ? (row.Level ?? "unknown") : level;
            row.Bot = string.IsNullOrWhiteSpace(bot) ? (row.Bot ?? "unknown") : bot;
            row.Platform = platform ?? row.Platform;
            row.Kills = string.IsNullOrWhiteSpace(kills) ? (row.Kills ?? "unknown") : kills;
            if (!string.IsNullOrWhiteSpace(teamIndex))
            {
                if (teamIndexValue.HasValue &&
                    row.TeamIndexValue.HasValue &&
                    row.TeamIndexValue != teamIndexValue)
                {
                    row.TeamIndex = "unknown";
                    row.TeamIndexValue = null;
                    row.HasConflictingTeamIndex = true;
                }
                else if (teamIndexValue.HasValue && !row.HasConflictingTeamIndex)
                {
                    row.TeamIndex = teamIndex;
                    row.TeamIndexValue = teamIndexValue;
                }
                else if (!row.TeamIndexValue.HasValue && !row.HasConflictingTeamIndex)
                {
                    row.TeamIndex = teamIndex;
                }
            }
            else
            {
                row.TeamIndex ??= "unknown";
            }
            row.DeathCause = DeathCauseHelper.GetDisplayName(death, deathTagStrings) is var resolved && resolved != "Unknown"
                ? resolved
                : (row.DeathCause ?? "Unknown");
            row.Placement = string.IsNullOrWhiteSpace(placement) ? null : placement;
            row.Pickaxe = pickaxe;
            row.Glider = glider;

            // Build numericId → PlayerRow mapping (was Pass 4)
            var numericId = ReflectionUtils.FirstString(pd, "Id");
            if (!string.IsNullOrWhiteSpace(numericId) && !string.IsNullOrWhiteSpace(id))
            {
                playersByNumericId[numericId] = row;
            }

            // Detect replay owner (was Pass 8). Keep the flag on the row so later projections do
            // not need to infer ownership from a mutable display name.
            if (ReflectionUtils.GetBool(pd, "IsReplayOwner"))
            {
                row.IsReplayOwner = true;
            }

        }

        var replayOwners = playersById.Values.Where(player => player.IsReplayOwner).Take(2).ToList();
        var replayOwner = replayOwners.Count == 1 ? replayOwners[0] : null;
        var ownerId = replayOwner?.StableId;
        var ownerName = replayOwner?.Name is { } recordedOwnerName && recordedOwnerName != "unknown"
            ? recordedOwnerName
            : null;
        int? ownerKills = int.TryParse(replayOwner?.Kills, out var parsedOwnerKills)
            ? parsedOwnerKills
            : null;
        var ownerTeamIndex = replayOwner?.TeamIndexValue;

        // === Compute squad sizes from TeamIndex grouping ===
        var squadSizeByTeamIndex = new Dictionary<int, int>();
        foreach (var row in playersById.Values)
        {
            if (row.TeamIndexValue.HasValue)
                squadSizeByTeamIndex[row.TeamIndexValue.Value] =
                    squadSizeByTeamIndex.GetValueOrDefault(row.TeamIndexValue.Value) + 1;
        }
        foreach (var row in playersById.Values)
        {
            if (row.TeamIndexValue.HasValue && squadSizeByTeamIndex.TryGetValue(row.TeamIndexValue.Value, out var sz))
                row.SquadSize = sz;
        }

        // === PASS 2: Team data (unchanged — small collection) ===
        var teamKillsByIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var teamPlacementByIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var team in result.TeamData ?? Enumerable.Empty<object>())
        {
            var teamIdx = ReflectionUtils.FirstString(team, "TeamIndex");
            var teamKills = ReflectionUtils.FirstString(team, "TeamKills");
            var teamPlacement = ReflectionUtils.FirstString(team, "Placement");

            if (!string.IsNullOrWhiteSpace(teamIdx))
            {
                if (!string.IsNullOrWhiteSpace(teamKills))
                    teamKillsByIndex[teamIdx] = teamKills;
                if (!string.IsNullOrWhiteSpace(teamPlacement))
                    teamPlacementByIndex[teamIdx] = teamPlacement;
            }
        }

        // === Extract winning team info from GameData ===
        int? winningTeam = null;
        var winningPlayerIds = new List<string>();
        var winningPlayerNames = new List<string>();

        if (result.GameData != null)
        {
            var winTeamObj = ReflectionUtils.GetObject(result.GameData, "WinningTeam");
            if (winTeamObj is int wt)
                winningTeam = wt;
            else if (int.TryParse(winTeamObj?.ToString(), out var parsed))
                winningTeam = parsed;

            var winIds = ReflectionUtils.GetObject(result.GameData, "WinningPlayerIds");
            if (winIds is System.Collections.IEnumerable enumerable)
            {
                foreach (var id in enumerable)
                {
                    var idStr = id?.ToString();
                    if (!string.IsNullOrWhiteSpace(idStr))
                    {
                        winningPlayerIds.Add(idStr);
                        if (playersByNumericId.TryGetValue(idStr, out var player) && !string.IsNullOrWhiteSpace(player.Name))
                            winningPlayerNames.Add(player.Name);
                    }
                }
            }
        }

        var winTeamStr = winningTeam?.ToString();

        // === PASS 3: Apply team data + mark winners (merged from 3 passes into 1) ===
        var eliminationCount = 0; // Count finishes instead of building unused display strings
        foreach (var row in playersById.Values)
        {
            if (!string.IsNullOrWhiteSpace(row.TeamIndex))
            {
                if (teamKillsByIndex.TryGetValue(row.TeamIndex, out var teamKills))
                    row.TeamKills = teamKills;
                if (string.IsNullOrWhiteSpace(row.Placement) && teamPlacementByIndex.TryGetValue(row.TeamIndex, out var teamPlacement))
                    row.Placement = teamPlacement;

                // Mark winning team placement (was Pass 6)
                if (winTeamStr != null && row.TeamIndex == winTeamStr && string.IsNullOrWhiteSpace(row.Placement))
                    row.Placement = "1";
            }

            // Winners never died — show "N/A Won Match" instead of "Unknown" (was Pass 7)
            if (row.IsWinner && row.DeathCause == "Unknown")
                row.DeathCause = "N/A Won Match";
        }

        // === PASS 4: Process eliminations ===
        var ownerEliminations = new List<PlayerRow>();
        var pendingKnocks = new Dictionary<string, TimeSpan?>(StringComparer.OrdinalIgnoreCase);
        var ownerKnockTimes = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        string? ownerEliminatedBy = null;

        foreach (var elim in result.Eliminations ?? Enumerable.Empty<object>())
        {
            var isKnock = ReflectionUtils.GetBool(elim, "Knocked");
            var eliminatedId = ReflectionUtils.FirstString(
                ReflectionUtils.GetObject(elim, "EliminatedInfo"), "Id")
                ?? ReflectionUtils.FirstString(elim, "Eliminated")
                ?? "unknown";
            var eliminatorId = ReflectionUtils.FirstString(
                ReflectionUtils.GetObject(elim, "EliminatorInfo"), "Id")
                ?? ReflectionUtils.FirstString(elim, "Eliminator")
                ?? "unknown";

            var isOwnerAction = !string.IsNullOrEmpty(ownerId) &&
                                eliminatorId.Equals(ownerId, StringComparison.OrdinalIgnoreCase);

            var timeObj = ReflectionUtils.GetObject(elim, "Time");
            var eventTime = ParseElimTime(timeObj);
            var eventTimeStr = FormatElimTime(eventTime, timeObj);

            if (isKnock)
            {
                pendingKnocks[eliminatedId] = eventTime;
                if (isOwnerAction && eventTime.HasValue)
                    ownerKnockTimes[eliminatedId] = eventTime.Value;
                else if (!isOwnerAction)
                    ownerKnockTimes.Remove(eliminatedId);
            }
            else
            {
                // Finish event — count it and record time on the eliminated player
                eliminationCount++;

                var circle = stormCircleResolver.Resolve(GetEventReplayTimeSeconds(elim));

                // Set elimination time (and circle) on the player row
                if (playersById.TryGetValue(eliminatedId, out var elimRow))
                {
                    if (eventTimeStr != null)
                        elimRow.ElimTime = eventTimeStr;
                    // A repeated finish is a fresh observation. Clear a stale circle when
                    // this event has no trustworthy phase instead of retaining an older death.
                    elimRow.CircleNumber = circle.CircleNumber;
                    elimRow.CircleStatus = circle.Status;
                }

                // Track who eliminated the replay owner
                if (!string.IsNullOrEmpty(ownerId) &&
                    eliminatedId.Equals(ownerId, StringComparison.OrdinalIgnoreCase) &&
                    !eliminatorId.Equals(ownerId, StringComparison.OrdinalIgnoreCase))
                {
                    ownerEliminatedBy = playersById.TryGetValue(eliminatorId, out var eliminatorRow)
                        ? eliminatorRow.Name ?? eliminatorRow.Id
                        : eliminatorId;
                }

                // Credit owner for this elimination?
                var creditOwner = false;

                // A pending knock is stale if >60s have passed (player was revived)
                var hasActiveKnock = pendingKnocks.TryGetValue(eliminatedId, out var knockedAt)
                    && (!knockedAt.HasValue || !eventTime.HasValue
                        || (eventTime.Value - knockedAt.Value).TotalSeconds <= 60);

                if (!hasActiveKnock && isOwnerAction)
                {
                    creditOwner = true;
                }
                else if (ownerKnockTimes.TryGetValue(eliminatedId, out var knockTime)
                         && eventTime.HasValue
                         && (eventTime.Value - knockTime).TotalSeconds <= 60)
                {
                    creditOwner = true;
                }

                // A self-elimination is useful for preventing false kill credit, but it is
                // not evidence of recorder identity: any player can eliminate themselves.
                if (creditOwner
                    && !eliminatedId.Equals(ownerId, StringComparison.OrdinalIgnoreCase)
                    && playersById.TryGetValue(eliminatedId, out var victim))
                {
                    ownerEliminations.Add(new PlayerRow
                    {
                        StableId = victim.StableId,
                        Id = victim.Id,
                        Name = victim.Name,
                        Level = victim.Level,
                        Bot = victim.Bot,
                        Platform = victim.Platform,
                        Kills = victim.Kills,
                        TeamIndex = victim.TeamIndex,
                        TeamIndexValue = victim.TeamIndexValue,
                        HasConflictingTeamIndex = victim.HasConflictingTeamIndex,
                        DeathCause = victim.DeathCause,
                        Placement = victim.Placement,
                        ElimTime = eventTimeStr,
                        Pickaxe = victim.Pickaxe,
                        Glider = victim.Glider,
                        SquadSize = victim.SquadSize,
                        CircleNumber = circle.CircleNumber,
                        CircleStatus = circle.Status,
                    });
                }

                pendingKnocks.Remove(eliminatedId);
                ownerKnockTimes.Remove(eliminatedId);
            }
        }

        // === Build metadata ===
        var playlist = result.GameData?.CurrentPlaylist ?? "";
        var gameMode = PlaylistHelper.GetDisplayNameWithFallback(playlist);
        var maxPlayers = result.GameData?.MaxPlayers;
        // Info.LengthInMs is the duration of the recorded replay. MatchEndTime is an absolute
        // game-clock value, so it cannot be treated as an elapsed duration without a matching start time.
        var recordingDuration = result.Info.LengthInMs / 60000.0;

        var nonNpcCount = 0;
        foreach (var p in playersById.Values)
            if (!p.IsNpc) nonNpcCount++;

        var metadata = new ReplayMetadata
        {
            FileName = Path.GetFileName(path),
            Version = result.Header.Branch ?? "",
            Changelist = result.Header.Changelist,
            GameNetProtocol = result.Header.GameNetworkProtocolVersion,
            PlayerCount = nonNpcCount,
            EliminationCount = eliminationCount,
            GameMode = gameMode,
            Playlist = playlist,
            MaxPlayers = maxPlayers,
            RecordingDurationMinutes = recordingDuration,
            WinningTeam = winningTeam,
            WinningPlayerIds = winningPlayerIds,
            WinningPlayerNames = winningPlayerNames
        };

        return new ReplayData
        {
            Players = playersById.Values.OrderBy(v => v.Name ?? v.Id).ToList(),
            OwnerEliminations = ownerEliminations,
            OwnerId = ownerId,
            OwnerTeamIndex = ownerTeamIndex,
            OwnerName = ownerName,
            OwnerKills = ownerKills,
            OwnerEliminatedBy = ownerEliminatedBy,
            Metadata = metadata
        };
    }

    private static double? GetEventReplayTimeSeconds(object elimination)
    {
        var info = ReflectionUtils.GetObject(elimination, "Info");
        var startTimeMilliseconds = ReflectionUtils.GetDouble(info, "StartTime");
        return startTimeMilliseconds.HasValue ? startTimeMilliseconds.Value / 1000d : null;
    }

    static string? FormatElimTime(TimeSpan? ts, object? rawTimeObj)
    {
        if (ts.HasValue)
            return $"{(int)ts.Value.TotalMinutes:D2}:{ts.Value.Seconds:D2}";
        // Fall back to the raw string if parsing failed but a value exists
        var raw = rawTimeObj?.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    static TimeSpan? ParseElimTime(object? timeObj)
    {
        if (timeObj is TimeSpan ts) return ts;
        var str = timeObj?.ToString();
        if (string.IsNullOrWhiteSpace(str)) return null;
        var parts = str.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var min) && int.TryParse(parts[1], out var sec))
            return TimeSpan.FromSeconds(min * 60 + sec);
        if (TimeSpan.TryParse(str, out var parsed)) return parsed;
        return null;
    }
}
