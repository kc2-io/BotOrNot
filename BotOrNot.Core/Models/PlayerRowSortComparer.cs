using System.Collections;

namespace BotOrNot.Core.Models;

/// <summary>
/// Self-contained comparer for PlayerRow objects. Returns the final desired
/// sort order directly — callers use OrderBy (never negate the result).
///
/// When <paramref name="descending"/> is true, non-unknown values are returned
/// in descending order. Unknown/null/empty selected values always sort to bottom
/// unless <paramref name="unknownsFirst"/> is true (then they sort to top).
/// Null PlayerRow objects always sort to bottom, regardless of either setting.
/// </summary>
public sealed class PlayerRowSortComparer : IComparer<PlayerRow>, IComparer
{
    private readonly Func<PlayerRow, string?> _selector;
    private readonly bool _numeric;
    private readonly bool _isBotField;
    private readonly bool _descending;
    private readonly bool _unknownsFirst;

    public PlayerRowSortComparer(
        Func<PlayerRow, string?> selector,
        bool descending = false,
        bool numeric = false,
        bool isBotField = false,
        bool unknownsFirst = false)
    {
        _selector = selector;
        _descending = descending;
        _numeric = numeric;
        _isBotField = isBotField;
        _unknownsFirst = unknownsFirst;
    }

    public int Compare(PlayerRow? x, PlayerRow? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var vx = _selector(x);
        var vy = _selector(y);

        var xUnknown = IsUnknownOrEmpty(vx);
        var yUnknown = IsUnknownOrEmpty(vy);

        if (xUnknown && yUnknown) return 0;
        if (xUnknown) return _unknownsFirst ? -1 : 1;
        if (yUnknown) return _unknownsFirst ? 1 : -1;

        int result;

        if (_isBotField)
            result = BotRank(vx!).CompareTo(BotRank(vy!));
        else if (_numeric)
            result = CompareNumeric(vx!, vy!);
        else
            result = string.Compare(vx, vy, StringComparison.OrdinalIgnoreCase);

        if (_descending)
            result = -result;

        return result;
    }

    int IComparer.Compare(object? x, object? y) => Compare(x as PlayerRow, y as PlayerRow);

    private static int CompareNumeric(string vx, string vy)
    {
        var xParsed = int.TryParse(vx, out var xNum);
        var yParsed = int.TryParse(vy, out var yNum);

        if (xParsed && yParsed) return xNum.CompareTo(yNum);
        if (xParsed) return -1;
        if (yParsed) return 1;

        return string.Compare(vx, vy, StringComparison.OrdinalIgnoreCase);
    }

    private static int BotRank(string value) => value.ToLowerInvariant() switch
    {
        "true" => 0,
        "false" => 1,
        _ => 2
    };

    public static bool IsUnknownOrEmpty(string? value) =>
        string.IsNullOrEmpty(value) ||
        value.Equals("unknown", StringComparison.OrdinalIgnoreCase);
}
