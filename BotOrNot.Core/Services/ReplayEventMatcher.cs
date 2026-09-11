using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

public sealed record ReplayEventCorrelation(
    IReadOnlyDictionary<int, PlayerStateEventEvidence> Matches,
    IReadOnlySet<int> RelatedObservationSequences);

/// <summary>Correlates event-chunk eliminations with player-state observations without guessing ties.</summary>
public static class ReplayEventMatcher
{
    // Measured maximum nearest-frame offset was 1.004 s across 543 elimination events in
    // the inspected BR/Reload/F1 corpus. The small margin accounts for float representation.
    public const double MatchToleranceSeconds = 1.1;

    public static ReplayEventCorrelation Correlate(
        IEnumerable<EliminationEventEvidence> eliminations,
        IEnumerable<PlayerStateEventEvidence> observations)
    {
        var events = eliminations.ToArray();
        var states = observations.ToArray();
        var related = new HashSet<int>();
        var preferred = new Dictionary<int, PlayerStateEventEvidence[]>();

        foreach (var elimination in events)
        {
            var candidates = states.Where(state => IsCandidate(elimination, state)).ToArray();
            foreach (var candidate in candidates) related.Add(candidate.Sequence);

            var exactActor = candidates.Where(state =>
                !string.IsNullOrWhiteSpace(state.ActorId) &&
                state.ActorId.Equals(elimination.ActorId, StringComparison.OrdinalIgnoreCase)).ToArray();
            preferred[elimination.Sequence] = exactActor.Length > 0 ? exactActor : candidates;
        }

        var observationUseCounts = preferred.Values
            .SelectMany(value => value.Select(item => item.Sequence))
            .GroupBy(sequence => sequence)
            .ToDictionary(group => group.Key, group => group.Count());
        var matches = new Dictionary<int, PlayerStateEventEvidence>();

        foreach (var elimination in events)
        {
            var candidates = preferred[elimination.Sequence];
            if (candidates.Length == 1 && observationUseCounts[candidates[0].Sequence] == 1)
                matches[elimination.Sequence] = candidates[0];
        }

        return new ReplayEventCorrelation(matches, related);
    }

    private static bool IsCandidate(EliminationEventEvidence elimination, PlayerStateEventEvidence state)
    {
        if (!elimination.ReplayTimeSeconds.HasValue || !state.ReplayTimeSeconds.HasValue ||
            !elimination.VictimId.Equals(state.VictimId, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(elimination.ReplayTimeSeconds.Value - state.ReplayTimeSeconds.Value) > MatchToleranceSeconds)
            return false;

        if (elimination.IsKnock ? state.IsDbno != true : state.IsDbno == true)
            return false;

        return string.IsNullOrWhiteSpace(state.ActorId) ||
               state.ActorId.Equals(elimination.ActorId, StringComparison.OrdinalIgnoreCase);
    }
}
