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
    private sealed record ReplayEliminationRecord(EliminationEventEvidence Evidence, object? RawTime);

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
            var legacyDeathCause = DeathCauseHelper.ResolveLegacy(death, deathTagStrings);
            if (legacyDeathCause.ResolutionStatus == DeathCauseResolutionStatus.Resolved ||
                row.DeathCauseInfo == null)
            {
                row.DeathCauseInfo = legacyDeathCause;
                row.DeathCause = legacyDeathCause.DisplayName;
            }
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
        var eliminationCount = 0;
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
            if (row.IsWinner && row.DeathCauseInfo?.ResolutionStatus != DeathCauseResolutionStatus.Resolved)
            {
                row.DeathCauseInfo = DeathCauseInfo.WonMatch;
                row.DeathCause = DeathCauseInfo.WonMatch.DisplayName;
            }
        }

        // === PASS 4: Correlate event-chunk eliminations with player-state evidence ===
        var ownerEliminations = new List<PlayerRow>();
        string? ownerEliminatedBy = null;

        var eliminationRecords = (result.Eliminations ?? Enumerable.Empty<object>())
            .Select((elimination, sequence) =>
            {
                var victimId = ReflectionUtils.FirstString(
                    ReflectionUtils.GetObject(elimination, "EliminatedInfo"), "Id")
                    ?? ReflectionUtils.FirstString(elimination, "Eliminated")
                    ?? "unknown";
                var actorId = ReflectionUtils.FirstString(
                    ReflectionUtils.GetObject(elimination, "EliminatorInfo"), "Id")
                    ?? ReflectionUtils.FirstString(elimination, "Eliminator")
                    ?? "unknown";
                return new ReplayEliminationRecord(
                    new EliminationEventEvidence(
                        sequence,
                        GetEventReplayTimeSeconds(elimination),
                        victimId,
                        actorId,
                        ReflectionUtils.GetBool(elimination, "Knocked"),
                        ReflectionUtils.GetInt(elimination, "GunType")),
                    ReflectionUtils.GetObject(elimination, "Time"));
            })
            .ToArray();

        string? ResolveNumericPlayerId(object? numericId)
        {
            var numericText = numericId?.ToString();
            return !string.IsNullOrWhiteSpace(numericText) &&
                   playersByNumericId.TryGetValue(numericText, out var player)
                ? player.StableId
                : null;
        }

        var playerStateEvidence = new List<PlayerStateEventEvidence>();
        var killFeed = ReflectionUtils.GetObject(result, "KillFeed") as System.Collections.IEnumerable;
        if (killFeed != null)
        {
            var sequence = 0;
            foreach (var entry in killFeed)
            {
                var victimId = ResolveNumericPlayerId(ReflectionUtils.GetObject(entry, "PlayerId"));
                if (!string.IsNullOrWhiteSpace(victimId))
                {
                    playerStateEvidence.Add(new PlayerStateEventEvidence(
                        sequence,
                        ReflectionUtils.GetDouble(entry, "ReplayTimeSeconds"),
                        victimId,
                        ResolveNumericPlayerId(ReflectionUtils.GetObject(entry, "FinisherOrDowner")),
                        ReflectionUtils.GetNullableBool(entry, "IsDbno"),
                        ReflectionUtils.GetInt(entry, "RebootCounter"),
                        ReflectionUtils.GetInt(entry, "DeathCause"),
                        GetStringValues(entry, "DeathTags")));
                }
                sequence++;
            }
        }

        var correlation = ReplayEventMatcher.Correlate(
            eliminationRecords.Select(record => record.Evidence),
            playerStateEvidence);
        var lifecycle = new List<CombatLifecycleEvent>(eliminationRecords.Length + playerStateEvidence.Count);
        var causeBySequence = new Dictionary<int, DeathCauseInfo>();

        foreach (var record in eliminationRecords)
        {
            correlation.Matches.TryGetValue(record.Evidence.Sequence, out var matchedState);
            var cause = DeathCauseHelper.ResolveEvent(
                record.Evidence.RawCode,
                matchedState?.RawDeathCause,
                matchedState?.DeathTags);
            causeBySequence[record.Evidence.Sequence] = cause;
            lifecycle.Add(new CombatLifecycleEvent(
                record.Evidence.Sequence,
                record.Evidence.ReplayTimeSeconds,
                record.Evidence.IsKnock ? CombatLifecycleEventKind.Knock : CombatLifecycleEventKind.Finish,
                record.Evidence.VictimId,
                record.Evidence.ActorId,
                DbnoTrueObserved: matchedState?.IsDbno == true,
                DeathCause: cause));
        }

        foreach (var state in playerStateEvidence)
        {
            // A DBNO field related to any elimination candidate is part of that event, not a
            // recovery. Ambiguous correlations remain unused. Reboot counters are still retained
            // so only a later observed increase can reset a life.
            var dbno = correlation.RelatedObservationSequences.Contains(state.Sequence)
                ? null
                : state.IsDbno;
            if (!dbno.HasValue && !state.RebootCounter.HasValue)
                continue;

            lifecycle.Add(new CombatLifecycleEvent(
                1_000_000 + state.Sequence,
                state.ReplayTimeSeconds,
                CombatLifecycleEventKind.PlayerState,
                state.VictimId,
                state.ActorId,
                dbno,
                state.RebootCounter));
        }

        eliminationCount = eliminationRecords.Count(record => !record.Evidence.IsKnock);
        foreach (var record in eliminationRecords.Where(record => !record.Evidence.IsKnock))
        {
            var evidence = record.Evidence;
            var eventTime = evidence.ReplayTimeSeconds.HasValue
                ? TimeSpan.FromSeconds(evidence.ReplayTimeSeconds.Value)
                : ParseElimTime(record.RawTime);
            var eventTimeStr = FormatElimTime(eventTime, record.RawTime);
            var circle = stormCircleResolver.Resolve(evidence.ReplayTimeSeconds);
            var cause = causeBySequence[evidence.Sequence];

            if (playersById.TryGetValue(evidence.VictimId, out var eliminatedPlayer))
            {
                if (eventTimeStr != null) eliminatedPlayer.ElimTime = eventTimeStr;
                eliminatedPlayer.DeathCauseInfo = cause;
                eliminatedPlayer.DeathCause = cause.DisplayName;
                eliminatedPlayer.SetStormCircle(circle);
            }

            if (!string.IsNullOrEmpty(ownerId) &&
                evidence.VictimId.Equals(ownerId, StringComparison.OrdinalIgnoreCase) &&
                !evidence.ActorId.Equals(ownerId, StringComparison.OrdinalIgnoreCase))
                ownerEliminatedBy = playersById.TryGetValue(evidence.ActorId, out var eliminatorRow)
                    ? eliminatorRow.Name ?? eliminatorRow.Id
                    : evidence.ActorId;
        }

        var decisions = OwnerEliminationResolver.Resolve(ownerId, lifecycle);
        foreach (var decision in decisions.Where(item => item.Status == OwnerCreditStatus.Credited))
        {
            var record = eliminationRecords.Single(item => item.Evidence.Sequence == decision.EventSequence);
            if (!playersById.TryGetValue(decision.VictimId, out var victim)) continue;
            var eventTime = record.Evidence.ReplayTimeSeconds.HasValue
                ? TimeSpan.FromSeconds(record.Evidence.ReplayTimeSeconds.Value)
                : ParseElimTime(record.RawTime);
            var circle = stormCircleResolver.Resolve(record.Evidence.ReplayTimeSeconds);

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
                DeathCauseInfo = decision.DeathCause,
                DeathCause = decision.DeathCause.DisplayName,
                Placement = victim.Placement,
                ElimTime = FormatElimTime(eventTime, record.RawTime),
                Pickaxe = victim.Pickaxe,
                Glider = victim.Glider,
                SquadSize = victim.SquadSize,
                CircleNumber = circle.CircleNumber,
                CircleStatus = circle.Status,
            });
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
            HasUncertainEliminationAttribution = decisions.Any(item => item.Status == OwnerCreditStatus.Uncertain),
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

    private static IReadOnlyList<string> GetStringValues(object? source, string propertyName)
    {
        var value = ReflectionUtils.GetObject(source, propertyName);
        if (value is not System.Collections.IEnumerable enumerable) return Array.Empty<string>();

        var values = new List<string>();
        foreach (var item in enumerable)
        {
            var text = item?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) values.Add(text);
        }
        return values;
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
