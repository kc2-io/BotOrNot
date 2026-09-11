using System.Globalization;
using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

public enum ParticipantRelationship
{
    Owner,
    Teammate,
    Opponent,
    Unknown
}

/// <summary>Classifies one participant using only authoritative owner and team evidence.</summary>
public static class ParticipantClassifier
{
    /// <summary>
    /// The parser exposes team indices as nullable integers and only adopts values greater than
    /// zero. Replay fixtures include valid values above 100, so no upper bound is imposed here.
    /// </summary>
    public static int? NormalizeTeamIndex(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
               && parsed > 0
            ? parsed
            : null;
    }

    public static ParticipantRelationship Classify(ReplayData replay, PlayerRow participant)
    {
        var owners = replay.Players.Where(player => player.IsReplayOwner).Take(2).ToList();
        if (owners.Count != 1)
            return ParticipantRelationship.Unknown;

        var owner = owners[0];
        if (!string.Equals(owner.StableId, replay.OwnerId, StringComparison.OrdinalIgnoreCase) ||
            owner.TeamIndexValue != replay.OwnerTeamIndex)
        {
            return ParticipantRelationship.Unknown;
        }

        if (participant.IsReplayOwner ||
            (!string.IsNullOrWhiteSpace(replay.OwnerId) &&
             !string.IsNullOrWhiteSpace(participant.StableId) &&
             participant.StableId.Equals(replay.OwnerId, StringComparison.OrdinalIgnoreCase)))
        {
            return ParticipantRelationship.Owner;
        }

        if (!replay.OwnerTeamIndex.HasValue ||
            !participant.TeamIndexValue.HasValue ||
            participant.HasConflictingTeamIndex)
            return ParticipantRelationship.Unknown;

        return participant.TeamIndexValue == replay.OwnerTeamIndex
            ? ParticipantRelationship.Teammate
            : ParticipantRelationship.Opponent;
    }
}
