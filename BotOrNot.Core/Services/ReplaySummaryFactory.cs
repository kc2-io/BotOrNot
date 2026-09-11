using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

public static class ReplaySummaryFactory
{
    /// <summary>Creates the complete cache projection for one parsed replay.</summary>
    public static ReplaySummary Create(ReplayData data, FileInfo file)
    {
        var nonNpc = data.Players.Where(player => !player.IsNpc).ToList();
        var ownerEliminations = data.OwnerEliminations.Where(player => !player.IsNpc).ToList();
        var ownerPlayers = data.Players.Where(player => player.IsReplayOwner).Take(2).ToList();
        var ownerPlayer = ownerPlayers.Count == 1 ? ownerPlayers[0] : null;
        var analysisStatus = ownerPlayer == null || string.IsNullOrWhiteSpace(data.OwnerId)
            ? ReplayAnalysisStatus.OwnerIdentityUnavailable
            : !data.OwnerKills.HasValue
                ? ReplayAnalysisStatus.OwnerKillsUnavailable
                : data.HasUncertainEliminationAttribution || ownerEliminations.Count != data.OwnerKills.Value
                    ? ReplayAnalysisStatus.EliminationAttributionIncomplete
                    : ReplayAnalysisStatus.Complete;
        var opponentProjection = OpponentProjection.FromReplay(data);

        return new ReplaySummary
        {
            FileName = file.Name,
            FilePath = file.FullName,
            FileDate = file.LastWriteTimeUtc,
            GameMode = data.Metadata.GameMode,
            Playlist = data.Metadata.Playlist,
            Placement = ownerPlayer?.Placement ?? "",
            Kills = analysisStatus == ReplayAnalysisStatus.OwnerIdentityUnavailable ||
                    analysisStatus == ReplayAnalysisStatus.OwnerKillsUnavailable
                ? null
                : data.OwnerKills,
            BotKills = analysisStatus == ReplayAnalysisStatus.Complete
                ? ownerEliminations.Count(player => player.IsBot)
                : null,
            PlayerCount = nonNpc.Count,
            BotCount = nonNpc.Count(player => player.IsBot),
            DurationMinutes = data.Metadata.RecordingDurationMinutes,
            OwnerName = data.OwnerName ?? "",
            AnalysisStatus = analysisStatus,
            Opponents = opponentProjection.Opponents,
            OpponentAnalysisComplete = opponentProjection.IsComplete
        };
    }
}
