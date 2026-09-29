using System.Text.Json.Serialization;

namespace BotOrNot.Core.Models;

public sealed class PlayerRow
{
    /// <summary>
    /// Parser-provided account identity that can be compared across replay files. This remains
    /// null when the row's display <see cref="Id"/> had to fall back to a name or generated value.
    /// </summary>
    public string? StableId { get; set; }
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Level { get; set; }
    public string? Bot { get; set; }
    public string? Platform { get; set; }
    public string? Kills { get; set; }
    public string? TeamKills { get; set; }
    public string? TeamIndex { get; set; }
    /// <summary>
    /// Positive numeric team index recorded by the parser. Zero, negative, missing, and
    /// non-numeric values are unavailable rather than team identities.
    /// </summary>
    public int? TeamIndexValue { get; set; }
    /// <summary>True when repeated records for this identity supplied conflicting valid teams.</summary>
    public bool HasConflictingTeamIndex { get; set; }
    /// <summary>Whether the parser authoritatively identified this row as the replay recorder.</summary>
    public bool IsReplayOwner { get; set; }
    /// <summary>Structured evidence for <see cref="DeathCause"/>, when available.</summary>
    public DeathCauseInfo? DeathCauseInfo { get; set; }
    public string? DeathCause { get; set; }
    public string? Placement { get; set; }
    public string? ElimTime { get; set; }
    public string? Pickaxe { get; set; }
    public string? Glider { get; set; }
    public int SquadSize { get; set; }
    /// <summary>True when the replay recorder is credited with eliminating this row.</summary>
    public bool KilledByOwner { get; set; }

    public int? CircleNumber { get; set; }
    public StormCircleStatus CircleStatus { get; set; }

    [JsonIgnore]
    public string StormPhaseDisplay => CircleStatus switch
    {
        StormCircleStatus.RecordedPhase when CircleNumber.HasValue => $"Phase {CircleNumber.Value}",
        StormCircleStatus.BeforeFirstCircle => "Before phase 1",
        _ => "Unknown"
    };

    [JsonIgnore]
    public string StormPhaseTooltip => CircleStatus switch
    {
        StormCircleStatus.RecordedPhase when CircleNumber.HasValue =>
            $"The replay recorded storm phase {CircleNumber.Value} at this elimination.",
        StormCircleStatus.BeforeFirstCircle =>
            "The replay explicitly recorded phase 0 at this elimination.",
        _ => "No trustworthy storm phase had been recorded by this elimination."
    };

    [JsonIgnore]
    public string? StormPhaseSortValue => CircleStatus switch
    {
        StormCircleStatus.RecordedPhase when CircleNumber.HasValue => CircleNumber.Value.ToString(),
        StormCircleStatus.BeforeFirstCircle => "0",
        _ => null
    };

    [JsonIgnore]
    public string StormPhaseCsvValue => StormPhaseDisplay;

    public void SetStormCircle(StormCircleResolution resolution)
    {
        CircleNumber = resolution.CircleNumber;
        CircleStatus = resolution.Status;
    }

    public bool IsBot => !string.IsNullOrEmpty(Bot) && Bot.Equals("true", StringComparison.OrdinalIgnoreCase);
    public bool IsWinner => Placement == "1";

    /// <summary>
    /// NPCs either expose their name as a stable ID or have no stable account ID while the
    /// parser identifies them as bots. A name-derived display ID alone is not NPC evidence.
    /// </summary>
    public bool IsNpc => !string.IsNullOrEmpty(Name) &&
                         ((!string.IsNullOrEmpty(StableId) &&
                           StableId.Equals(Name, StringComparison.OrdinalIgnoreCase)) ||
                          (string.IsNullOrEmpty(StableId) && IsBot &&
                           Id.Equals(Name, StringComparison.OrdinalIgnoreCase)));
}
