using BotOrNot.Core.Models;

namespace BotOrNot.Tests;

[TestFixture]
public class PlayerRowSortComparerTests
{
    private static PlayerRow Row(string? kills = null, string? bot = null, string? placement = null, string? name = null)
        => new() { Kills = kills, Bot = bot, Placement = placement, Name = name };

    // --- Numeric sorting (Kills, Level, Placement, etc.) ---

    [Test]
    public void Numeric_Ascending_SortsAsIntegers()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: false, numeric: true);
        var rows = new[]
        {
            Row(kills: "10"), Row(kills: "2"), Row(kills: "9"), Row(kills: "1"), Row(kills: "20")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Kills).ToList();

        Assert.That(sorted, Is.EqualTo(new[] { "1", "2", "9", "10", "20" }));
    }

    [Test]
    public void Numeric_Descending_SortsAsIntegers()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: true, numeric: true);
        var rows = new[]
        {
            Row(kills: "10"), Row(kills: "2"), Row(kills: "9"), Row(kills: "1"), Row(kills: "20")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Kills).ToList();

        Assert.That(sorted, Is.EqualTo(new[] { "20", "10", "9", "2", "1" }));
    }

    // --- Unknown always at bottom ---

    [Test]
    public void Unknown_Ascending_SortsToBottom()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: false, numeric: true);
        var rows = new[]
        {
            Row(kills: "unknown"), Row(kills: "5"), Row(kills: "1"), Row(kills: null), Row(kills: "10")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Kills).ToList();

        Assert.That(sorted, Is.EqualTo(new[] { "1", "5", "10", "unknown", null }));
    }

    [Test]
    public void Unknown_Descending_StillSortsToBottom()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: true, numeric: true);
        var rows = new[]
        {
            Row(kills: "unknown"), Row(kills: "5"), Row(kills: "1"), Row(kills: null), Row(kills: "10")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Kills).ToList();

        // Known values descending, then Unknown/null at bottom
        Assert.That(sorted[0], Is.EqualTo("10"));
        Assert.That(sorted[1], Is.EqualTo("5"));
        Assert.That(sorted[2], Is.EqualTo("1"));
        // Last two are unknown/null in any order
        Assert.That(sorted[3], Is.AnyOf("unknown", null));
        Assert.That(sorted[4], Is.AnyOf("unknown", null));
    }

    [Test]
    public void Unknown_CaseInsensitive()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: false, numeric: true);

        var unknown = Row(kills: "Unknown");
        var known = Row(kills: "5");

        Assert.That(comparer.Compare(unknown, known), Is.GreaterThan(0));
    }

    // --- Bot field sorting ---

    [Test]
    public void Bot_Ascending_SortsTrueThenFalse()
    {
        var comparer = new PlayerRowSortComparer(p => p.Bot, descending: false, isBotField: true);
        var rows = new[]
        {
            Row(bot: "false"), Row(bot: "true"), Row(bot: "false"), Row(bot: "true")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Bot).ToList();

        Assert.That(sorted, Is.EqualTo(new[] { "true", "true", "false", "false" }));
    }

    [Test]
    public void Bot_UnknownAlwaysLast()
    {
        var comparer = new PlayerRowSortComparer(p => p.Bot, descending: false, isBotField: true);
        var rows = new[]
        {
            Row(bot: "unknown"), Row(bot: "false"), Row(bot: "true"), Row(bot: null)
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Bot).ToList();

        Assert.That(sorted[0], Is.EqualTo("true"));
        Assert.That(sorted[1], Is.EqualTo("false"));
        // Last two are unknown/null
        Assert.That(sorted[2], Is.AnyOf("unknown", null));
        Assert.That(sorted[3], Is.AnyOf("unknown", null));
    }

    [Test]
    public void Bot_Descending_UnknownStillLast()
    {
        var comparer = new PlayerRowSortComparer(p => p.Bot, descending: true, isBotField: true);
        var rows = new[]
        {
            Row(bot: "unknown"), Row(bot: "false"), Row(bot: "true"), Row(bot: null)
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Bot).ToList();

        Assert.That(sorted[0], Is.EqualTo("false"));
        Assert.That(sorted[1], Is.EqualTo("true"));
        // Last two are unknown/null
        Assert.That(sorted[2], Is.AnyOf("unknown", null));
        Assert.That(sorted[3], Is.AnyOf("unknown", null));
    }

    // --- Unknowns-first mode (mode 3 of 3-mode cycle) ---

    [Test]
    public void UnknownsFirst_Ascending_PushesUnknownsToTop()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: false, numeric: true, unknownsFirst: true);
        var rows = new[]
        {
            Row(kills: "5"), Row(kills: "unknown"), Row(kills: "1"), Row(kills: null), Row(kills: "10")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Kills).ToList();

        // Unknowns first, then numeric values ascending
        Assert.That(sorted[0], Is.AnyOf("unknown", null));
        Assert.That(sorted[1], Is.AnyOf("unknown", null));
        Assert.That(sorted[2], Is.EqualTo("1"));
        Assert.That(sorted[3], Is.EqualTo("5"));
        Assert.That(sorted[4], Is.EqualTo("10"));
    }

    [Test]
    public void UnknownsFirst_Descending_StillPushesUnknownsToTop()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: true, numeric: true, unknownsFirst: true);
        var rows = new[]
        {
            Row(kills: "5"), Row(kills: "unknown"), Row(kills: "1"), Row(kills: null), Row(kills: "10")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Kills).ToList();

        // Unknowns first, then numeric values descending
        Assert.That(sorted[0], Is.AnyOf("unknown", null));
        Assert.That(sorted[1], Is.AnyOf("unknown", null));
        Assert.That(sorted[2], Is.EqualTo("10"));
        Assert.That(sorted[3], Is.EqualTo("5"));
        Assert.That(sorted[4], Is.EqualTo("1"));
    }

    [Test]
    public void UnknownsFirst_NoUnknowns_StillSortsNumerically()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: false, numeric: true, unknownsFirst: true);
        var rows = new[]
        {
            Row(kills: "10"), Row(kills: "2"), Row(kills: "5")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Kills).ToList();

        Assert.That(sorted, Is.EqualTo(new[] { "2", "5", "10" }));
    }

    // --- Unknowns-first for non-numeric fields ---

    [Test]
    public void UnknownsFirst_String_PushesNullsToTop()
    {
        var comparer = new PlayerRowSortComparer(p => p.Name, descending: false, unknownsFirst: true);
        var rows = new[]
        {
            Row(name: "Charlie"), Row(name: null), Row(name: "Alice"), Row(name: "Bob")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Name).ToList();

        Assert.That(sorted, Is.EqualTo(new[] { null, "Alice", "Bob", "Charlie" }));
    }

    [Test]
    public void UnknownsFirst_Bot_PushesUnknownsToTop()
    {
        var comparer = new PlayerRowSortComparer(p => p.Bot, descending: false, isBotField: true, unknownsFirst: true);
        var rows = new[]
        {
            Row(bot: "false"), Row(bot: "unknown"), Row(bot: "true"), Row(bot: null)
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Bot).ToList();

        Assert.That(sorted[0], Is.AnyOf("unknown", null));
        Assert.That(sorted[1], Is.AnyOf("unknown", null));
        Assert.That(sorted[2], Is.EqualTo("true"));
        Assert.That(sorted[3], Is.EqualTo("false"));
    }

    [Test]
    public void UnknownsFirst_String_Descending_StillPushesNullsToTop()
    {
        var comparer = new PlayerRowSortComparer(p => p.Name, descending: true, unknownsFirst: true);
        var rows = new[]
        {
            Row(name: "Charlie"), Row(name: null), Row(name: "Alice"), Row(name: "Bob")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Name).ToList();

        Assert.That(sorted[0], Is.EqualTo(null));
        Assert.That(sorted[1], Is.EqualTo("Charlie"));
        Assert.That(sorted[2], Is.EqualTo("Bob"));
        Assert.That(sorted[3], Is.EqualTo("Alice"));
    }

    // --- String sorting ---

    [Test]
    public void String_Ascending_SortsAlphabetically()
    {
        var comparer = new PlayerRowSortComparer(p => p.Name, descending: false);
        var rows = new[]
        {
            Row(name: "Charlie"), Row(name: "Alice"), Row(name: "Bob")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Name).ToList();

        Assert.That(sorted, Is.EqualTo(new[] { "Alice", "Bob", "Charlie" }));
    }

    // --- Placement (numeric with nulls) ---

    [Test]
    public void Placement_NullPushedToBottom()
    {
        var comparer = new PlayerRowSortComparer(p => p.Placement, descending: false, numeric: true);
        var rows = new[]
        {
            Row(placement: null), Row(placement: "1"), Row(placement: "50"), Row(placement: "3")
        };

        var sorted = rows.OrderBy(r => r, comparer).Select(r => r.Placement).ToList();

        Assert.That(sorted, Is.EqualTo(new[] { "1", "3", "50", null }));
    }

    // --- Null PlayerRow handling ---

    [Test]
    public void NullRows_SortToBottom()
    {
        var comparer = new PlayerRowSortComparer(p => p.Kills, descending: false, numeric: true);

        Assert.That(comparer.Compare(null, Row(kills: "5")), Is.GreaterThan(0));
        Assert.That(comparer.Compare(Row(kills: "5"), null), Is.LessThan(0));
        Assert.That(comparer.Compare((PlayerRow?)null, null), Is.EqualTo(0));
    }

    [Test]
    public void UnknownPairs_CompareEqualInEverySortMode(
        [Values(null, "", "UnKnOwN")] string? left,
        [Values(null, "", "UnKnOwN")] string? right,
        [Values] bool descending,
        [Values] bool unknownsFirst)
    {
        var comparer = new PlayerRowSortComparer(
            p => p.Name, descending: descending, unknownsFirst: unknownsFirst);

        Assert.That(comparer.Compare(Row(name: left), Row(name: right)), Is.Zero);
    }

    [Test]
    public void UnknownPlacement_IsIndependentOfDirectionAndFieldType(
        [Values("text", "numeric", "bot")] string fieldType,
        [Values] bool descending,
        [Values] bool unknownsFirst)
    {
        var comparer = new PlayerRowSortComparer(
            p => p.Name, descending: descending, numeric: fieldType == "numeric",
            isBotField: fieldType == "bot", unknownsFirst: unknownsFirst);
        var known = Row(name: fieldType switch { "numeric" => "5", "bot" => "true", _ => "Alice" });
        var expectedSign = unknownsFirst ? -1 : 1;

        foreach (var value in new string?[] { null, "", "UnKnOwN" })
        {
            var unknown = Row(name: value);
            Assert.Multiple(() =>
            {
                Assert.That(Math.Sign(comparer.Compare(unknown, known)), Is.EqualTo(expectedSign));
                Assert.That(Math.Sign(comparer.Compare(known, unknown)), Is.EqualTo(-expectedSign));
            });
        }
    }

    [Test]
    public void NullRows_AlwaysSortLastWithoutInvokingSelector(
        [Values] bool descending,
        [Values] bool unknownsFirst)
    {
        var comparer = new PlayerRowSortComparer(
            _ => throw new InvalidOperationException("Null-row comparisons must not select values."),
            descending: descending, unknownsFirst: unknownsFirst);

        Assert.Multiple(() =>
        {
            Assert.That(comparer.Compare(null, Row(name: "Alice")), Is.GreaterThan(0));
            Assert.That(comparer.Compare(Row(name: "Alice"), null), Is.LessThan(0));
            Assert.That(comparer.Compare(null, Row(name: null)), Is.GreaterThan(0));
            Assert.That(comparer.Compare(Row(name: null), null), Is.LessThan(0));
            Assert.That(comparer.Compare(null, null), Is.Zero);
        });
    }

    [TestCase("   ")]
    [TestCase(" unknown ")]
    public void WhitespaceAndPaddedUnknown_AreKnownValues(string value)
    {
        Assert.That(PlayerRowSortComparer.IsUnknownOrEmpty(value), Is.False);
    }

    // --- IComparer (non-generic) interface ---

    [Test]
    public void NonGeneric_IComparer_Works()
    {
        var comparer = (System.Collections.IComparer)new PlayerRowSortComparer(
            p => p.Kills, descending: false, numeric: true);

        var a = Row(kills: "2");
        var b = Row(kills: "10");

        Assert.That(comparer.Compare(a, b), Is.LessThan(0));
    }
}
