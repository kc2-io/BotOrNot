namespace BotOrNot.Core.Models;

public enum ReplayAnalysisStatus
{
    Complete,
    OwnerIdentityUnavailable,
    OwnerKillsUnavailable
}

public sealed class OpponentSummary
{
    public string StableId { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ReplaySummary
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime FileDate { get; set; }
    public string GameMode { get; set; } = "";
    public string Playlist { get; set; } = "";
    public string Placement { get; set; } = "";
    /// <summary>
    /// The owner's authoritative kill count. It is null when owner analysis is incomplete,
    /// which is distinct from a confirmed zero-kill match.
    /// </summary>
    public int? Kills { get; set; }
    public int? BotKills { get; set; }
    public int PlayerCount { get; set; }
    public int BotCount { get; set; }
    public double DurationMinutes { get; set; }
    public string OwnerName { get; set; } = "";
    public List<OpponentSummary> Opponents { get; set; } = new();
    /// <summary>
    /// False when missing owner, identity, or team evidence prevented a definitive relationship
    /// for one or more human participants.
    /// </summary>
    public bool OpponentAnalysisComplete { get; set; }
    public ReplayAnalysisStatus AnalysisStatus { get; set; }

    public int? PlayerKills => Kills.HasValue && BotKills.HasValue ? Kills - BotKills : null;
    public string AnalysisStatusText => AnalysisStatus switch
    {
        ReplayAnalysisStatus.Complete => "Complete",
        ReplayAnalysisStatus.OwnerIdentityUnavailable => "Owner unknown",
        ReplayAnalysisStatus.OwnerKillsUnavailable => "Owner kills unknown",
        _ => "Incomplete"
    };
    public bool IsWin => Placement == "1";
    public double BotPercent => PlayerCount > 0 ? (double)BotCount / PlayerCount * 100 : 0;
}
