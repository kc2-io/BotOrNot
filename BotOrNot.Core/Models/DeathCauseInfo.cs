namespace BotOrNot.Core.Models;

public enum DeathCauseCategory
{
    Unknown,
    PlayerWeapon,
    Environment,
    NonCombat,
    Winner
}

public enum DeathCauseSource
{
    None,
    EliminationEventCode,
    KillFeedCode,
    SpecificEventTag,
    LegacyPlayerSnapshot,
    LegacyPlayerSnapshotTag,
    DerivedWinner
}

public enum DeathCauseResolutionStatus
{
    Resolved,
    Unknown,
    Conflicting
}

public enum EvidenceConfidence
{
    None,
    Low,
    Medium,
    High
}

public enum DeathCauseTagRole
{
    None,
    KillFeedDeathContext,
    LegacyPlayerSnapshot
}

/// <summary>
/// Structured death-cause evidence. Event and kill-feed codes remain separate so a conflict
/// is reviewable instead of being collapsed into one guessed value.
/// </summary>
public sealed record DeathCauseInfo
{
    public DeathCauseCategory Category { get; init; } = DeathCauseCategory.Unknown;
    public string DisplayName { get; init; } = "Unknown";
    public int? RawEventCode { get; init; }
    public int? RawKillFeedCode { get; init; }
    public IReadOnlyList<string> RawTags { get; init; } = Array.Empty<string>();
    public DeathCauseTagRole TagRole { get; init; }
    public DeathCauseSource Source { get; init; }
    public DeathCauseResolutionStatus ResolutionStatus { get; init; } = DeathCauseResolutionStatus.Unknown;
    public EvidenceConfidence Confidence { get; init; }

    public string EvidenceSummary
    {
        get
        {
            var parts = new List<string>
            {
                $"Source: {Source}",
                $"Confidence: {Confidence}",
                $"Status: {ResolutionStatus}"
            };
            if (RawEventCode.HasValue) parts.Add($"Event code: {RawEventCode.Value}");
            if (RawKillFeedCode.HasValue) parts.Add($"Kill-feed code: {RawKillFeedCode.Value}");
            if (RawTags.Count > 0) parts.Add($"{TagRole} tags: {string.Join(", ", RawTags)}");
            return string.Join(Environment.NewLine, parts);
        }
    }

    public static DeathCauseInfo Unknown { get; } = new();

    public static DeathCauseInfo WonMatch { get; } = new()
    {
        Category = DeathCauseCategory.Winner,
        DisplayName = "N/A Won Match",
        Source = DeathCauseSource.DerivedWinner,
        ResolutionStatus = DeathCauseResolutionStatus.Resolved,
        Confidence = EvidenceConfidence.High
    };
}
