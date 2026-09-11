namespace BotOrNot.Core.Models;

public sealed class ReplayData
{
    public List<PlayerRow> Players { get; set; } = new();
    [Obsolete("Use Metadata.EliminationCount instead. This list is no longer populated.")]
    public List<string> Eliminations { get; set; } = new();
    public List<PlayerRow> OwnerEliminations { get; set; } = new();
    /// <summary>The recorder's parser-provided stable account ID, when available.</summary>
    public string? OwnerId { get; set; }
    /// <summary>The recorder's validated positive team index, when available.</summary>
    public int? OwnerTeamIndex { get; set; }
    public string? OwnerName { get; set; }
    /// <summary>
    /// Authoritative kill count from the owner's PlayerData.Kills property.
    /// </summary>
    public int? OwnerKills { get; set; }
    /// <summary>
    /// True when one or more finish events could not be safely attributed to or away from the owner.
    /// The authoritative scalar kill count remains available, but derived kill rows are incomplete.
    /// </summary>
    public bool HasUncertainEliminationAttribution { get; set; }
    public string? OwnerEliminatedBy { get; set; }
    public ReplayMetadata Metadata { get; set; } = new();
}

public sealed class ReplayMetadata
{
    public string FileName { get; set; } = "";
    /// <summary>The replay header branch, when present.</summary>
    public string Version { get; set; } = "";
    /// <summary>The replay header changelist. A value of zero is retained as recorded.</summary>
    public uint Changelist { get; set; }
    /// <summary>The replay header game network protocol. A value of zero is retained as recorded.</summary>
    public uint GameNetProtocol { get; set; }
    public int PlayerCount { get; set; }
    public int EliminationCount { get; set; }
    public string GameMode { get; set; } = "";
    public string Playlist { get; set; } = "";
    public int? MaxPlayers { get; set; }
    /// <summary>
    /// Length of the recording in minutes. This is not necessarily the elapsed match duration:
    /// a replay can begin late or stop before the match ends.
    /// </summary>
    public double RecordingDurationMinutes { get; set; }
    public int? WinningTeam { get; set; }
    public List<string> WinningPlayerIds { get; set; } = new();
    public List<string> WinningPlayerNames { get; set; } = new();
}
