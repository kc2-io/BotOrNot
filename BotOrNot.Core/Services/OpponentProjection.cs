using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

public sealed class OpponentProjection
{
    public List<OpponentSummary> Opponents { get; init; } = new();
    public bool IsComplete { get; init; }

    /// <summary>
    /// Produces definitive human opponents for one replay. Entries are unique by stable account
    /// ID within the match; owners, teammates, bots, NPCs, and unknown relationships are absent.
    /// </summary>
    public static OpponentProjection FromReplay(ReplayData replay)
    {
        var owners = replay.Players.Where(player => player.IsReplayOwner).Take(2).ToList();
        var owner = owners.Count == 1 ? owners[0] : null;
        var isComplete = owner != null &&
                         !string.IsNullOrWhiteSpace(replay.OwnerId) &&
                         string.Equals(owner.StableId, replay.OwnerId, StringComparison.OrdinalIgnoreCase) &&
                         replay.OwnerTeamIndex.HasValue &&
                         owner.TeamIndexValue == replay.OwnerTeamIndex &&
                         !owner.HasConflictingTeamIndex;
        var opponents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var participants = replay.Players.Where(player => !player.IsBot && !player.IsNpc).ToList();
        var ambiguousStableIds = FindAmbiguousStableIds(replay, participants);

        foreach (var participant in participants)
        {
            var relationship = ParticipantClassifier.Classify(replay, participant);
            if (relationship == ParticipantRelationship.Owner)
                continue;

            if (relationship == ParticipantRelationship.Unknown ||
                (!string.IsNullOrWhiteSpace(participant.StableId) &&
                 ambiguousStableIds.Contains(participant.StableId)) ||
                string.IsNullOrWhiteSpace(participant.StableId))
            {
                isComplete = false;
                continue;
            }

            if (relationship == ParticipantRelationship.Teammate)
                continue;

            if (!opponents.TryGetValue(participant.StableId, out var names))
            {
                names = new List<string>();
                opponents[participant.StableId] = names;
            }

            if (!string.IsNullOrWhiteSpace(participant.Name))
                names.Add(participant.Name);
        }

        return new OpponentProjection
        {
            IsComplete = isComplete,
            Opponents = opponents
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new OpponentSummary
                {
                    StableId = entry.Key,
                    Name = entry.Value
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(name => name, StringComparer.Ordinal)
                        .LastOrDefault() ?? ""
                })
                .ToList()
        };
    }

    private static HashSet<string> FindAmbiguousStableIds(ReplayData replay, IEnumerable<PlayerRow> participants)
    {
        // Different teams can still agree on an opponent relationship; explicit conflicts always exclude the identity.
        return participants
            .Where(player => !string.IsNullOrWhiteSpace(player.StableId))
            .GroupBy(player => player.StableId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Any(player => player.HasConflictingTeamIndex) ||
                            group.Select(player => ParticipantClassifier.Classify(replay, player)).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
