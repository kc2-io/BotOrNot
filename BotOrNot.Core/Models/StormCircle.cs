namespace BotOrNot.Core.Models;

/// <summary>Meaning of the parser's latest replicated storm phase at an event time.</summary>
public enum StormCircleStatus
{
    Unknown,
    BeforeFirstCircle,
    RecordedPhase
}

/// <summary>
/// A time-local storm phase observation. Replay time is the decoder frame clock and is
/// directly comparable with elimination EventInfo.StartTime / 1000.
/// </summary>
public readonly record struct StormCircleObservation(
    double ReplayTimeSeconds,
    int? CurrentPhase,
    int? PhaseCount,
    uint? ChannelIndex = null,
    uint? ActorGuid = null);

public readonly record struct StormCircleResolution(StormCircleStatus Status, int? CircleNumber)
{
    public static StormCircleResolution Unknown { get; } = new(StormCircleStatus.Unknown, null);
    public static StormCircleResolution BeforeFirstCircle { get; } =
        new(StormCircleStatus.BeforeFirstCircle, null);

    public static StormCircleResolution ForRecordedPhase(int phase) =>
        new(StormCircleStatus.RecordedPhase, phase);
}
