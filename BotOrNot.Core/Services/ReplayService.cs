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

    // Reuse a single no-op logger for the replay reader instead of creating a LoggerFactory per call
    private static readonly ILogger<ReplayReader> ReaderLogger = NullLoggerFactory.Instance.CreateLogger<ReplayReader>();

    public ReplayService(ILogger<ReplayService>? logger = null)
    {
        _logger = logger ?? NullLogger<ReplayService>.Instance;
    }

    public async Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
    {
        var reader = new ReplayReader(ReaderLogger, ParseMode.Normal);

        var result = await Task.Run(() => reader.ReadReplay(path), cancellationToken).ConfigureAwait(false);

        // Pre-size dictionaries for typical Fortnite lobby (~100 players)
        var playersById = new Dictionary<string, PlayerRow>(128, StringComparer.OrdinalIgnoreCase);
        var playersByNumericId = new Dictionary<string, PlayerRow>(128, StringComparer.OrdinalIgnoreCase);

        // Owner detection — collected during the single PlayerData pass
        string? ownerId = null;
        string? ownerName = null;
        int? ownerKills = null;
        object? fallbackOwnerPd = null;
        int fallbackMaxLocations = 0;

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

            row.Name = string.IsNullOrWhiteSpace(name) ? (row.Name ?? "unknown") : name;
            row.Level = string.IsNullOrWhiteSpace(level) ? (row.Level ?? "unknown") : level;
            row.Bot = string.IsNullOrWhiteSpace(bot) ? (row.Bot ?? "unknown") : bot;
            row.Platform = platform ?? row.Platform;
            row.Kills = string.IsNullOrWhiteSpace(kills) ? (row.Kills ?? "unknown") : kills;
            row.TeamIndex = string.IsNullOrWhiteSpace(teamIndex) ? (row.TeamIndex ?? "unknown") : teamIndex;
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

            // Detect replay owner (was Pass 8)
            if (ownerId == null && ReflectionUtils.GetBool(pd, "IsReplayOwner"))
            {
                ownerId = ReflectionUtils.FirstString(pd, "PlayerId", "EpicId", "Id");
                ownerName = ReflectionUtils.FirstString(pd, "PlayerName", "DisplayName", "Name");
                var killsStr = ReflectionUtils.FirstString(pd, "Kills");
                if (int.TryParse(killsStr, out var k))
                    ownerKills = k;
            }

            // Track fallback owner candidate (player with most locations)
            if (ownerId == null)
            {
                var locations = ReflectionUtils.GetObject(pd, "Locations");
                if (locations is System.Collections.IEnumerable enumerable)
                {
                    int count = 0;
                    foreach (var _ in enumerable) count++;
                    if (count > fallbackMaxLocations)
                    {
                        fallbackMaxLocations = count;
                        fallbackOwnerPd = pd;
                    }
                }
            }
        }

        // Apply fallback owner if primary detection didn't find one
        if (string.IsNullOrEmpty(ownerId) && fallbackOwnerPd != null)
        {
            ownerId = ReflectionUtils.FirstString(fallbackOwnerPd, "PlayerId", "EpicId", "Id");
            ownerName = ReflectionUtils.FirstString(fallbackOwnerPd, "PlayerName", "DisplayName", "Name");
            var killsStr = ReflectionUtils.FirstString(fallbackOwnerPd, "Kills");
            if (int.TryParse(killsStr, out var kills))
                ownerKills = kills;
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

                // Set elimination time on the player row
                if (playersById.TryGetValue(eliminatedId, out var elimRow) && eventTimeStr != null)
                    elimRow.ElimTime = eventTimeStr;

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

                if (creditOwner && playersById.TryGetValue(eliminatedId, out var victim))
                {
                    ownerEliminations.Add(new PlayerRow
                    {
                        Id = victim.Id,
                        Name = victim.Name,
                        Level = victim.Level,
                        Bot = victim.Bot,
                        Platform = victim.Platform,
                        Kills = victim.Kills,
                        TeamIndex = victim.TeamIndex,
                        DeathCause = victim.DeathCause,
                        Placement = victim.Placement,
                        ElimTime = eventTimeStr,
                        Pickaxe = victim.Pickaxe,
                        Glider = victim.Glider
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
        // result.Info was removed in FortniteReplayReader v3.x; MatchEndTime (seconds) is the closest equivalent
        var matchDuration = (double)(result.GameData?.MatchEndTime ?? 0f) / 60.0;

        var nonNpcCount = 0;
        foreach (var p in playersById.Values)
            if (!p.IsNpc) nonNpcCount++;

        var metadata = new ReplayMetadata
        {
            FileName = Path.GetFileName(path),
            Version = "",
            GameNetProtocol = "",
            PlayerCount = nonNpcCount,
            EliminationCount = eliminationCount,
            GameMode = gameMode,
            Playlist = playlist,
            MaxPlayers = maxPlayers,
            MatchDurationMinutes = matchDuration,
            WinningTeam = winningTeam,
            WinningPlayerIds = winningPlayerIds,
            WinningPlayerNames = winningPlayerNames
        };

        return new ReplayData
        {
            Players = playersById.Values.OrderBy(v => v.Name ?? v.Id).ToList(),
            OwnerEliminations = ownerEliminations,
            OwnerName = ownerName,
            OwnerKills = ownerKills,
            OwnerEliminatedBy = ownerEliminatedBy,
            Metadata = metadata
        };
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
