namespace BotOrNot.Core.Models;

public sealed class PlayerRow
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Level { get; set; }
    public string? Bot { get; set; }
    public string? Platform { get; set; }
    public string? Kills { get; set; }
    public string? TeamKills { get; set; }
    public string? TeamIndex { get; set; }
    public string? DeathCause { get; set; }
    public string? Placement { get; set; }
    public string? ElimTime { get; set; }
    public string? Pickaxe { get; set; }
    public string? Glider { get; set; }
    public int SquadSize { get; set; }

    public int? CircleNumber { get; set; }

    public bool IsBot => !string.IsNullOrEmpty(Bot) && Bot.Equals("true", StringComparison.OrdinalIgnoreCase);
    public bool IsWinner => Placement == "1";

    /// <summary>
    /// NPCs have their Player ID equal to their Player Name.
    /// AI players and real players have unique IDs.
    /// </summary>
    public bool IsNpc => !string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(Name) &&
                         Id.Equals(Name, StringComparison.OrdinalIgnoreCase);
}