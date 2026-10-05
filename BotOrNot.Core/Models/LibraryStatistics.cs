namespace BotOrNot.Core.Models;

public sealed record LibraryStatistics(
    int Matches, int Wins, double WinRate, double? AverageKills, double AverageBotPercent)
{
    public static LibraryStatistics From(IReadOnlyCollection<ReplaySummary> replays)
    {
        var wins = replays.Count(replay => replay.IsWin);
        var knownKills = replays.Select(replay => replay.Kills).OfType<int>().ToArray();
        return new(replays.Count, wins,
            replays.Count > 0 ? (double)wins / replays.Count * 100 : 0,
            knownKills.Length > 0 ? knownKills.Average() : null,
            replays.Count > 0 ? replays.Average(replay => replay.BotPercent) : 0);
    }
}
