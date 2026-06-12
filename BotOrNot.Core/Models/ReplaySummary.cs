namespace BotOrNot.Core.Models;

public sealed class ReplaySummary
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime FileDate { get; set; }
    public string GameMode { get; set; } = "";
    public string Playlist { get; set; } = "";
    public string Placement { get; set; } = "";
    public int Kills { get; set; }
    public int BotKills { get; set; }
    public int PlayerCount { get; set; }
    public int BotCount { get; set; }
    public double DurationMinutes { get; set; }
    public string OwnerName { get; set; } = "";
    public List<string> PlayerNames { get; set; } = new();

    public int PlayerKills => Kills - BotKills;
    public bool IsWin => Placement == "1";
    public double BotPercent => PlayerCount > 0 ? (double)BotCount / PlayerCount * 100 : 0;
}
