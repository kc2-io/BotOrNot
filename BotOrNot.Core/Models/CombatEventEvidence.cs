namespace BotOrNot.Core.Models;

public sealed record EliminationEventEvidence(
    int Sequence,
    double? ReplayTimeSeconds,
    string VictimId,
    string ActorId,
    bool IsKnock,
    int? RawCode);

public sealed record PlayerStateEventEvidence(
    int Sequence,
    double? ReplayTimeSeconds,
    string VictimId,
    string? ActorId,
    bool? IsDbno,
    int? RebootCounter,
    int? RawDeathCause,
    IReadOnlyList<string> DeathTags);

public enum CombatLifecycleEventKind
{
    Knock,
    Finish,
    PlayerState
}

public sealed record CombatLifecycleEvent(
    int Sequence,
    double? ReplayTimeSeconds,
    CombatLifecycleEventKind Kind,
    string VictimId,
    string? ActorId = null,
    bool? IsDbno = null,
    int? RebootCounter = null,
    bool DbnoTrueObserved = false,
    DeathCauseInfo? DeathCause = null);

public enum OwnerCreditStatus
{
    Credited,
    NotCredited,
    Uncertain
}

public enum OwnerCreditSource
{
    DirectFinish,
    OwnerKnock,
    OtherPlayerKnock,
    SelfElimination,
    AmbiguousLifecycle,
    OtherPlayerFinish
}

public sealed record OwnerEliminationDecision(
    int EventSequence,
    string VictimId,
    OwnerCreditStatus Status,
    OwnerCreditSource Source,
    DeathCauseInfo DeathCause);
