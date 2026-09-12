using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

/// <summary>
/// Resolves the latest storm phase that had actually been replicated by an event's replay time.
/// It does not infer a phase from future observations or compare replay time with world time.
/// </summary>
public sealed class StormCircleResolver
{
    private readonly TimelineEntry[] _timeline;
    private readonly bool _hasAmbiguousSources;

    public StormCircleResolver(IEnumerable<StormCircleObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var ordered = observations
            .Select((observation, sourceOrder) => new IndexedObservation(observation, sourceOrder))
            .Where(item => double.IsFinite(item.Observation.ReplayTimeSeconds) &&
                           item.Observation.ReplayTimeSeconds >= 0)
            .OrderBy(item => item.Observation.ReplayTimeSeconds)
            .ThenBy(item => item.SourceOrder)
            .ToArray();

        _hasAmbiguousSources = CountIdentifiedSources(ordered) > 1;

        var timeline = new List<TimelineEntry>();
        for (var index = 0; index < ordered.Length;)
        {
            var replayTime = ordered[index].Observation.ReplayTimeSeconds;
            var groupEnd = index + 1;
            while (groupEnd < ordered.Length &&
                   ordered[groupEnd].Observation.ReplayTimeSeconds.Equals(replayTime))
            {
                groupEnd++;
            }

            timeline.Add(new TimelineEntry(replayTime, ResolveTie(ordered.AsSpan(index, groupEnd - index))));
            index = groupEnd;
        }

        _timeline = timeline.ToArray();
    }

    public StormCircleResolution Resolve(double? eventReplayTimeSeconds)
    {
        if (!eventReplayTimeSeconds.HasValue ||
            !double.IsFinite(eventReplayTimeSeconds.Value) ||
            eventReplayTimeSeconds.Value < 0 ||
            _timeline.Length == 0 ||
            _hasAmbiguousSources)
        {
            return StormCircleResolution.Unknown;
        }

        var low = 0;
        var high = _timeline.Length - 1;
        var match = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_timeline[middle].ReplayTimeSeconds <= eventReplayTimeSeconds.Value)
            {
                match = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        // Before the first replicated phase is unknown. A later phase must never be
        // backfilled into an earlier event, even when it is the first captured observation.
        return match < 0 ? StormCircleResolution.Unknown : _timeline[match].Resolution;
    }

    private static StormCircleResolution ResolveTie(ReadOnlySpan<IndexedObservation> observations)
    {
        // Elimination events do not carry ordering within one replay frame. Identical
        // repeats are harmless, while any phase disagreement at that frame is ambiguous.
        var candidates = observations
            .ToArray()
            .Select(item => ResolveObservation(item.Observation))
            .Distinct()
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : StormCircleResolution.Unknown;
    }

    private static int CountIdentifiedSources(IndexedObservation[] observations)
    {
        var actorsByChannel = observations
            .Where(item => item.Observation.ChannelIndex.HasValue && item.Observation.ActorGuid.HasValue)
            .GroupBy(item => item.Observation.ChannelIndex!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Observation.ActorGuid!.Value).Distinct().ToArray());

        var sources = new HashSet<SourceIdentity>();
        foreach (var item in observations)
        {
            var observation = item.Observation;
            if (observation.ActorGuid is uint actorGuid)
            {
                sources.Add(new SourceIdentity(true, actorGuid));
            }
            else if (observation.ChannelIndex is uint channelIndex &&
                     actorsByChannel.TryGetValue(channelIndex, out var knownActors) &&
                     knownActors.Length == 1)
            {
                sources.Add(new SourceIdentity(true, knownActors[0]));
            }
            else if (observation.ChannelIndex is uint unidentifiedChannel)
            {
                sources.Add(new SourceIdentity(false, unidentifiedChannel));
            }
        }

        return sources.Count;
    }

    private static StormCircleResolution ResolveObservation(StormCircleObservation observation)
    {
        if (observation.CurrentPhase is not int phase || phase < 0)
            return StormCircleResolution.Unknown;

        if (phase == 0)
            return StormCircleResolution.BeforeFirstCircle;

        if (observation.PhaseCount is int phaseCount &&
            (phaseCount <= 0 || phase > phaseCount))
        {
            return StormCircleResolution.Unknown;
        }

        return StormCircleResolution.ForRecordedPhase(phase);
    }

    private readonly record struct IndexedObservation(
        StormCircleObservation Observation,
        int SourceOrder);

    private readonly record struct TimelineEntry(
        double ReplayTimeSeconds,
        StormCircleResolution Resolution);

    private readonly record struct SourceIdentity(bool IsActor, uint Id);
}
