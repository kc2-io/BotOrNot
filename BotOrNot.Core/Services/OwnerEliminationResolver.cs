using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

/// <summary>Attributes finishes to one stable participant from an explicit DBNO/reboot lifecycle.</summary>
public static class ParticipantEliminationResolver
{
    private sealed class VictimState
    {
        public string? KnockerId { get; set; }
        public bool DbnoTrueObserved { get; set; }
        public int? LastRebootCounter { get; set; }
        public bool IsAmbiguous { get; set; }
        public HashSet<string> CandidateActorIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<OwnerEliminationDecision> Resolve(
        string? participantId,
        IEnumerable<CombatLifecycleEvent> source)
    {
        if (string.IsNullOrWhiteSpace(participantId)) return Array.Empty<OwnerEliminationDecision>();

        var states = new Dictionary<string, VictimState>(StringComparer.OrdinalIgnoreCase);
        var decisions = new List<OwnerEliminationDecision>();
        var allEvents = source.ToArray();
        var ordered = allEvents
            .Where(item => IsValidTime(item.ReplayTimeSeconds))
            .OrderBy(item => item.ReplayTimeSeconds)
            .ThenBy(item => item.Sequence)
            .ToArray();

        for (var index = 0; index < ordered.Length;)
        {
            var end = index + 1;
            if (ordered[index].ReplayTimeSeconds.HasValue)
            {
                while (end < ordered.Length && ordered[end].ReplayTimeSeconds == ordered[index].ReplayTimeSeconds)
                    end++;
            }

            ProcessGroup(participantId, ordered.AsSpan(index, end - index), states, decisions);
            index = end;
        }

        // Unknown clocks cannot inherit or consume a timeline built from known clocks. A direct
        // owner finish remains direct evidence; attribution through any knock is uncertain.
        foreach (var item in allEvents.Where(item => !IsValidTime(item.ReplayTimeSeconds) &&
                                                     item.Kind == CombatLifecycleEventKind.Finish))
            decisions.Add(ResolveUnorderedFinish(participantId, item));

        return decisions.OrderBy(decision => decision.EventSequence).ToArray();
    }

    public static bool HasRelevantUncertainty(
        string? participantId,
        IEnumerable<OwnerEliminationDecision> decisions) =>
        !string.IsNullOrWhiteSpace(participantId) && decisions.Any(decision =>
            decision.Status == OwnerCreditStatus.Uncertain &&
            !decision.VictimId.Equals(participantId, StringComparison.OrdinalIgnoreCase) &&
            (decision.CandidateParticipantIds == null ||
             decision.CandidateParticipantIds.Contains(
                 participantId,
                 StringComparer.OrdinalIgnoreCase)));

    private static void ProcessGroup(
        string ownerId,
        ReadOnlySpan<CombatLifecycleEvent> events,
        Dictionary<string, VictimState> states,
        List<OwnerEliminationDecision> decisions)
    {
        var groupEvents = events.ToArray();
        var ambiguousVictims = groupEvents
            .Where(item => item.Kind == CombatLifecycleEventKind.Finish)
            .GroupBy(item => item.VictimId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ambiguousKnockVictims = groupEvents
            .Where(item => item.Kind == CombatLifecycleEventKind.Knock)
            .GroupBy(item => item.VictimId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.ActorId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatesAtGroupStart = states.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.CandidateActorIds.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var groupCandidateActors = groupEvents
            .GroupBy(item => item.VictimId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.ActorId)
                    .Concat(candidatesAtGroupStart.GetValueOrDefault(group.Key) ?? Array.Empty<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var finish in events)
        {
            if (finish.Kind != CombatLifecycleEventKind.Finish)
                continue;

            states.TryGetValue(finish.VictimId, out var state);
            state ??= new VictimState();

            if (groupEvents.Any(item => item.Kind == CombatLifecycleEventKind.Knock &&
                                        item.VictimId.Equals(finish.VictimId, StringComparison.OrdinalIgnoreCase)))
                ambiguousVictims.Add(finish.VictimId);

            foreach (var item in events)
            {
                if (item.Kind == CombatLifecycleEventKind.PlayerState &&
                    item.VictimId.Equals(finish.VictimId, StringComparison.OrdinalIgnoreCase) &&
                    IsReset(item, state))
                {
                    ambiguousVictims.Add(finish.VictimId);
                    break;
                }
            }
        }

        foreach (var item in events)
        {
            if (!states.TryGetValue(item.VictimId, out var state))
            {
                state = new VictimState();
                states[item.VictimId] = state;
            }

            switch (item.Kind)
            {
                case CombatLifecycleEventKind.Knock:
                    if (!ambiguousKnockVictims.Contains(item.VictimId))
                        state.CandidateActorIds.Clear();
                    if (!string.IsNullOrWhiteSpace(item.ActorId))
                        state.CandidateActorIds.Add(item.ActorId);
                    state.KnockerId = item.ActorId;
                    state.DbnoTrueObserved = item.DbnoTrueObserved;
                    state.IsAmbiguous = ambiguousKnockVictims.Contains(item.VictimId);
                    break;

                case CombatLifecycleEventKind.PlayerState:
                    ApplyPlayerState(item, state);
                    break;

                case CombatLifecycleEventKind.Finish:
                    decisions.Add(ResolveFinish(ownerId, item, state,
                        ambiguousVictims.Contains(item.VictimId) || state.IsAmbiguous,
                        groupCandidateActors.GetValueOrDefault(item.VictimId)));
                    state.KnockerId = null;
                    state.DbnoTrueObserved = false;
                    state.IsAmbiguous = false;
                    state.CandidateActorIds.Clear();
                    break;
            }
        }

        // A tied finish/knock or reset has no trustworthy resulting life state. Do not let
        // source order leave a fresh-looking knock that can receive credit at a later finish.
        // A subsequently observed knock or explicit reset can establish a new lifecycle.
        foreach (var victimId in ambiguousVictims)
        {
            var state = states[victimId];
            state.KnockerId = null;
            state.DbnoTrueObserved = false;
            state.IsAmbiguous = true;
            if (groupCandidateActors.TryGetValue(victimId, out var candidateActors))
            {
                foreach (var candidateActor in candidateActors)
                    state.CandidateActorIds.Add(candidateActor);
            }
        }
    }

    private static OwnerEliminationDecision ResolveUnorderedFinish(string ownerId, CombatLifecycleEvent item)
    {
        var cause = item.DeathCause ?? DeathCauseInfo.Unknown;
        if (item.VictimId.Equals(ownerId, StringComparison.OrdinalIgnoreCase) ||
            item.VictimId.Equals(item.ActorId, StringComparison.OrdinalIgnoreCase))
            return new(item.Sequence, item.VictimId, OwnerCreditStatus.NotCredited,
                OwnerCreditSource.SelfElimination, cause);

        if (item.ActorId?.Equals(ownerId, StringComparison.OrdinalIgnoreCase) == true)
            return new(item.Sequence, item.VictimId, OwnerCreditStatus.Credited,
                OwnerCreditSource.DirectFinish, cause);

        return new(item.Sequence, item.VictimId, OwnerCreditStatus.Uncertain,
            OwnerCreditSource.AmbiguousLifecycle, cause);
    }

    private static OwnerEliminationDecision ResolveFinish(
        string ownerId,
        CombatLifecycleEvent item,
        VictimState state,
        bool ambiguousLifecycle,
        IReadOnlyList<string>? groupCandidateActors)
    {
        var cause = item.DeathCause ?? DeathCauseInfo.Unknown;
        if (ambiguousLifecycle)
        {
            var candidateIds = state.CandidateActorIds
                .Concat(groupCandidateActors ?? Array.Empty<string>())
                .Append(item.ActorId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new(item.Sequence, item.VictimId, OwnerCreditStatus.Uncertain,
                OwnerCreditSource.AmbiguousLifecycle, cause,
                candidateIds.Length == 0 || candidateIds.Contains("unknown", StringComparer.OrdinalIgnoreCase)
                    ? null
                    : candidateIds);
        }

        if (item.VictimId.Equals(ownerId, StringComparison.OrdinalIgnoreCase) ||
            item.VictimId.Equals(item.ActorId, StringComparison.OrdinalIgnoreCase))
            return new(item.Sequence, item.VictimId, OwnerCreditStatus.NotCredited,
                OwnerCreditSource.SelfElimination, cause);

        if (!string.IsNullOrWhiteSpace(state.KnockerId))
        {
            var ownerKnocked = state.KnockerId.Equals(ownerId, StringComparison.OrdinalIgnoreCase);
            return new(item.Sequence, item.VictimId,
                ownerKnocked ? OwnerCreditStatus.Credited : OwnerCreditStatus.NotCredited,
                ownerKnocked ? OwnerCreditSource.OwnerKnock : OwnerCreditSource.OtherPlayerKnock,
                cause);
        }

        var ownerFinished = item.ActorId?.Equals(ownerId, StringComparison.OrdinalIgnoreCase) == true;
        return new(item.Sequence, item.VictimId,
            ownerFinished ? OwnerCreditStatus.Credited : OwnerCreditStatus.NotCredited,
            ownerFinished ? OwnerCreditSource.DirectFinish : OwnerCreditSource.OtherPlayerFinish,
            cause);
    }

    private static void ApplyPlayerState(CombatLifecycleEvent item, VictimState state)
    {
        if (!item.ReplayTimeSeconds.HasValue) return;

        var resetByReboot = false;
        if (item.RebootCounter.HasValue)
        {
            resetByReboot = state.LastRebootCounter.HasValue &&
                            item.RebootCounter.Value > state.LastRebootCounter.Value;
            if (!state.LastRebootCounter.HasValue || item.RebootCounter.Value > state.LastRebootCounter.Value)
                state.LastRebootCounter = item.RebootCounter.Value;
        }

        if (item.IsDbno == true && !string.IsNullOrWhiteSpace(state.KnockerId))
            state.DbnoTrueObserved = true;

        if (resetByReboot || item.IsDbno == false && state.DbnoTrueObserved)
        {
            state.KnockerId = null;
            state.DbnoTrueObserved = false;
            state.IsAmbiguous = false;
            state.CandidateActorIds.Clear();
        }
    }

    private static bool IsReset(CombatLifecycleEvent item, VictimState state)
    {
        if (!item.ReplayTimeSeconds.HasValue) return false;
        return item.IsDbno == false && state.DbnoTrueObserved ||
               item.RebootCounter.HasValue && state.LastRebootCounter.HasValue &&
               item.RebootCounter.Value > state.LastRebootCounter.Value;
    }

    private static bool IsValidTime(double? time) =>
        time.HasValue && double.IsFinite(time.Value) && time.Value >= 0;
}

/// <summary>Compatibility entry point for existing owner-attribution callers.</summary>
public static class OwnerEliminationResolver
{
    public static IReadOnlyList<OwnerEliminationDecision> Resolve(
        string? ownerId,
        IEnumerable<CombatLifecycleEvent> source) =>
        ParticipantEliminationResolver.Resolve(ownerId, source);
}
