using System.Reflection;
using System.Collections;
using FortniteReplayReader;
using Microsoft.Extensions.Logging;
using Unreal.Core.Models.Enums;

string? replayPath = args.Length > 0 ? args[0] : null;

if (!string.IsNullOrEmpty(replayPath) && Directory.Exists(replayPath))
{
    await CompareElims.RunAsync(replayPath);
    return;
}

if (args.Length > 1 && args[0] == "--dump-owner")
{
    await DumpOwnerElims.RunAsync(args[1]);
    return;
}

if (args.Length > 1 && args[0] == "--dump-fields")
{
    DumpElimFields.Run(args[1]);
    return;
}

if (args.Length > 2 && args[0] == "--dump-player")
{
    DumpPlayerElims.Run(args[1], args[2]);
    return;
}

if (args.Length > 1 && args[0] == "--dump-all")
{
    string? outFile = args.Length > 2 ? args[2] : null;
    DumpAllElims.Run(args[1], outFile);
    return;
}

if (args.Length > 1 && args[0] == "--dump-unknown-deaths")
{
    DumpUnknownDeaths.Run(args[1]);
    return;
}

if (args.Length > 1 && args[0] == "--dump-debug")
{
    // Run with full debug logging to capture unmatched NetFieldExportGroup paths.
    // Logs go to stderr; stdout gets the GameData summary.
    using var lf = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));
    var rdr = new ReplayReader(lf.CreateLogger<ReplayReader>(), ParseMode.Full);
    var res = rdr.ReadReplay(args[1]);
    Console.WriteLine($"Players={res.PlayerData?.Count() ?? 0}");
    Console.WriteLine($"Eliminations={res.Eliminations?.Count ?? 0}");
    Console.WriteLine($"GameData.RecorderId='{res.GameData?.RecorderId}'");
    Console.WriteLine($"GameData.CurrentPlaylist='{res.GameData?.CurrentPlaylist}'");
    Console.WriteLine($"GameData.MaxPlayers={res.GameData?.MaxPlayers}");
    return;
}

if (args.Length > 2 && args[0] == "--dump-playerdata")
{
    var searchName = args[2];
    using var lf = LoggerFactory.Create(b => b.AddFilter(_ => false));
    var rdr = new ReplayReader(lf.CreateLogger<ReplayReader>(), ParseMode.Full);
    var res = rdr.ReadReplay(args[1]);
    int entryIdx = 0;
    foreach (var pd in res.PlayerData ?? Enumerable.Empty<object>())
    {
        var pname = pd.GetType().GetProperty("PlayerName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(pd)?.ToString();
        if (pname == null || !pname.Contains(searchName, StringComparison.OrdinalIgnoreCase)) { entryIdx++; continue; }
        Console.WriteLine($"=== PlayerData entry [{entryIdx}] for {pname} ===");
        DumpAllProperties(pd, "  ");
        // Expand DeathTags
        var dtObj = pd.GetType().GetProperty("DeathTags", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(pd);
        if (dtObj is IEnumerable dtEnum)
        {
            Console.WriteLine("  DeathTags (expanded):");
            foreach (var tag in dtEnum)
                Console.WriteLine($"    - {tag}");
        }
        Console.WriteLine();
        entryIdx++;
    }
    return;
}

if (args.Length > 1 && args[0] == "--compare-death")
{
    using var lf2 = LoggerFactory.Create(b => b.AddFilter(_ => false));
    var rdr2 = new ReplayReader(lf2.CreateLogger<ReplayReader>(), ParseMode.Full);
    var res2 = rdr2.ReadReplay(args[1]);

    // Build PlayerData lookup: id -> (name, DeathCause)
    var pdLookup = new Dictionary<string, (string name, string? deathCause)>(StringComparer.OrdinalIgnoreCase);
    foreach (var pd in res2.PlayerData ?? Enumerable.Empty<object>())
    {
        var id = pd.GetType().GetProperty("PlayerId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(pd)?.ToString();
        var name = pd.GetType().GetProperty("PlayerName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(pd)?.ToString();
        var dc = pd.GetType().GetProperty("DeathCause", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(pd)?.ToString();
        if (!string.IsNullOrEmpty(id))
            pdLookup[id] = (name ?? "?", dc);
    }

    Console.WriteLine($"{"Idx",-5} {"Victim",-25} {"GunType",-10} {"DeathCause",-12} {"Match?",-8}");
    Console.WriteLine(new string('-', 65));

    int idx2 = 0;
    int matches = 0, mismatches = 0, noData = 0;
    foreach (var elim in res2.Eliminations ?? Enumerable.Empty<object>())
    {
        var knocked = elim.GetType().GetProperty("Knocked", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(elim);
        if (knocked is true) { idx2++; continue; } // skip knocks

        var eInfo = elim.GetType().GetProperty("EliminatedInfo", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(elim);
        var elimId = eInfo?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(eInfo)?.ToString() ?? "?";
        var gunType = elim.GetType().GetProperty("GunType", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(elim)?.ToString() ?? "?";

        var victimName = pdLookup.TryGetValue(elimId, out var info) ? info.name : "?";
        var deathCause = info.deathCause ?? "(null)";

        string matchStr;
        if (!pdLookup.ContainsKey(elimId) || info.deathCause == null)
        {
            matchStr = "N/A";
            noData++;
        }
        else if (gunType == deathCause)
        {
            matchStr = "YES";
            matches++;
        }
        else
        {
            matchStr = "NO";
            mismatches++;
        }

        Console.WriteLine($"[{idx2,-3}] {victimName,-25} {gunType,-10} {deathCause,-12} {matchStr,-8}");
        idx2++;
    }

    Console.WriteLine(new string('-', 65));
    Console.WriteLine($"Matches: {matches}, Mismatches: {mismatches}, No data: {noData}");
    return;
}

if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath))
{
    Console.WriteLine("Please provide a path to a .replay file or directory as an argument");
    return;
}

Console.WriteLine($"=== ANALYZING: {Path.GetFileName(replayPath)} ===");
Console.WriteLine($"Path: {replayPath}\n");

using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
var logger = loggerFactory.CreateLogger<ReplayReader>();
var reader = new ReplayReader(logger, ParseMode.Full);
var result = reader.ReadReplay(replayPath);

// Key indicators
Console.WriteLine("KEY MODE INDICATORS:");
Console.WriteLine("--------------------");

// Match duration (result.Info removed in v3.x; MatchEndTime is in seconds)
Console.WriteLine($"Match Duration: {(result.GameData?.MatchEndTime ?? 0f) / 60.0:F1} minutes ({result.GameData?.MatchEndTime}s)");

// Player counts
var playerCount = result.PlayerData?.Count() ?? 0;
Console.WriteLine($"Total Players in Replay: {playerCount}");

// Team data
var teamCount = 0;
if (result.TeamData != null)
{
    foreach (var _ in result.TeamData) teamCount++;
}
Console.WriteLine($"Teams in Replay: {teamCount}");

// Eliminations
var elimCount = result.Eliminations?.Count ?? 0;
Console.WriteLine($"Total Eliminations: {elimCount}");

// GameData fields
Console.WriteLine($"\nGAMEDATA:");
Console.WriteLine($"  CurrentPlaylist: {result.GameData?.CurrentPlaylist ?? "(null)"}");
Console.WriteLine($"  MaxPlayers: {result.GameData?.MaxPlayers?.ToString() ?? "(null)"}");
Console.WriteLine($"  TeamSize: {result.GameData?.TeamSize?.ToString() ?? "(null)"}");
Console.WriteLine($"  TotalTeams: {result.GameData?.TotalTeams?.ToString() ?? "(null)"}");
Console.WriteLine($"  TotalBots: {result.GameData?.TotalBots?.ToString() ?? "(null)"}");
Console.WriteLine($"  IsLargeTeamGame: {result.GameData?.IsLargeTeamGame?.ToString() ?? "(null)"}");
Console.WriteLine($"  MapInfo: {result.GameData?.MapInfo ?? "(null)"}");
Console.WriteLine($"  GameSessionId: {result.GameData?.GameSessionId ?? "(null)"}");

// Check MapData
Console.WriteLine($"\nMAPDATA:");
if (result.MapData != null)
{
    DumpAllProperties(result.MapData, "  ");
}
else
{
    Console.WriteLine("  (null)");
}

// Header info (result.Header removed in v3.x)

// Look for unique team indices in player data
Console.WriteLine($"\nPLAYER TEAM ANALYSIS:");
var teamIndices = new HashSet<int>();
if (result.PlayerData != null)
{
    foreach (var player in result.PlayerData)
    {
        var teamIndex = GetPropValue<int?>(player, "TeamIndex");
        if (teamIndex.HasValue)
        {
            teamIndices.Add(teamIndex.Value);
        }
    }
}
Console.WriteLine($"  Unique TeamIndex values: {teamIndices.Count} ({string.Join(", ", teamIndices.OrderBy(x => x).Take(10))}{(teamIndices.Count > 10 ? "..." : "")})");

// Check for respawn indicators (Reload has respawns)
Console.WriteLine($"\nRESPAWN/REBOOT INDICATORS:");
var playersWithReboot = 0;
if (result.PlayerData != null)
{
    foreach (var player in result.PlayerData)
    {
        var rebootCounter = GetPropValue<int?>(player, "RebootCounter");
        if (rebootCounter.HasValue && rebootCounter.Value > 0)
        {
            playersWithReboot++;
        }
    }
}
Console.WriteLine($"  Players with RebootCounter > 0: {playersWithReboot}");

// Kill feed analysis
Console.WriteLine($"\nKILLFEED:");
Console.WriteLine($"  Total kills in feed: {result.KillFeed?.Count ?? 0}");

Console.WriteLine("\n" + new string('=', 50) + "\n");

void DumpAllProperties(object? obj, string indent)
{
    if (obj == null) return;
    var type = obj.GetType();
    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        try
        {
            var val = prop.GetValue(obj);
            if (val != null)
            {
                if (val is IEnumerable enumerable && !(val is string))
                {
                    var count = 0;
                    foreach (var _ in enumerable) count++;
                    Console.WriteLine($"{indent}{prop.Name}: [{count} items]");
                }
                else
                {
                    Console.WriteLine($"{indent}{prop.Name}: {val}");
                }
            }
            else
            {
                Console.WriteLine($"{indent}{prop.Name}: (null)");
            }
        }
        catch { }
    }
}

T? GetPropValue<T>(object? obj, string name)
{
    if (obj == null) return default;
    var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
    if (prop == null) return default;
    var val = prop.GetValue(obj);
    if (val is T t) return t;
    return default;
}