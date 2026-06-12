using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using BotOrNot.Core.Models;
using BotOrNot.UITests.Helpers;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public class DataGridSortingTests
{
    private static ObservableCollection<PlayerRow> CreateTestData() => new(new[]
    {
        new PlayerRow { Name = "Alice",   Kills = "10", Level = "50",      Placement = "1",  Bot = "false" },
        new PlayerRow { Name = "Bot1",    Kills = "2",  Level = "1",       Placement = "45", Bot = "true"  },
        new PlayerRow { Name = "Charlie", Kills = "9",  Level = "unknown", Placement = "3",  Bot = "unknown" },
        new PlayerRow { Name = "Diana",   Kills = "20", Level = "100",     Placement = null,  Bot = "false" },
        new PlayerRow { Name = "Eve",     Kills = null, Level = "25",      Placement = "10", Bot = "true"  },
    });

    private Window CreateWindowWithTestData()
    {
        var data = CreateTestData();
        return DataGridTestHelper.CreateMainWindowWithData(data, data);
    }

    // ── Numeric column with unknowns: Kills (3-mode) ────────────────
    // Mode cycle: click 1 = ascending (unknowns bottom),
    //             click 2 = unknowns first,
    //             click 3 = descending (unknowns bottom)

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Kills_Click1_SortsAscending(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Kills");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Kills);
        Assert.That(values, Is.EqualTo(new[] { "2", "9", "10", "20", null }));
    }

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Kills_Click2_UnknownsFirst(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Kills");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Kills");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Kills);
        Assert.That(values, Is.EqualTo(new[] { null, "2", "9", "10", "20" }));
    }

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Kills_Click3_SortsDescending(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Kills");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Kills");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Kills");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Kills);
        Assert.That(values, Is.EqualTo(new[] { "20", "10", "9", "2", null }));
    }

    // ── Numeric column with unknowns: Level (3-mode) ────────────────

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Level_Click1_SortsAscending(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Level");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Level);
        Assert.That(values, Is.EqualTo(new[] { "1", "25", "50", "100", "unknown" }));
    }

    // ── Numeric column with unknowns: Placement (3-mode) ────────────

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Placement_Click1_SortsAscending(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Place");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Placement);
        Assert.That(values, Is.EqualTo(new[] { "1", "3", "10", "45", null }));
    }

    // ── Bot column with unknowns (3-mode) ────────────────────────────
    // Mode cycle: click 1 = ascending (true→false, unknowns bottom),
    //             click 2 = unknowns first,
    //             click 3 = descending (false→true, unknowns bottom)

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Bot_Click1_SortsTrueThenFalse(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Bot");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Bot);
        Assert.That(values, Is.EqualTo(new[] { "true", "true", "false", "false", "unknown" }));
    }

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Bot_Click2_UnknownsFirst(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Bot");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Bot");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Bot);
        Assert.That(values, Is.EqualTo(new[] { "unknown", "true", "true", "false", "false" }));
    }

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Bot_Click3_SortsFalseThenTrue(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Bot");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Bot");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Bot");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Bot);
        Assert.That(values, Is.EqualTo(new[] { "false", "false", "true", "true", "unknown" }));
    }

    // ── Text column: Name (2-mode, no unknowns) ─────────────────────
    // Mode cycle: click 1 = ascending (A→Z),
    //             click 2 = descending (Z→A)

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Name_Click1_SortsAscending(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Name");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Name);
        Assert.That(values, Is.EqualTo(new[] { "Alice", "Bot1", "Charlie", "Diana", "Eve" }));
    }

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Name_Click2_SortsDescending(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Name");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Name");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Name);
        Assert.That(values, Is.EqualTo(new[] { "Eve", "Diana", "Charlie", "Bot1", "Alice" }));
    }

    [AvaloniaTest]
    [TestCase("OwnerEliminationsGrid")]
    [TestCase("PlayersGrid")]
    public void Name_Click3_WrapsToAscending(string gridName)
    {
        var window = CreateWindowWithTestData();
        var grid = DataGridTestHelper.GetDataGrid(window, gridName);

        DataGridTestHelper.ClickColumnHeader(window, grid, "Name");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Name");
        DataGridTestHelper.ClickColumnHeader(window, grid, "Name");

        var values = DataGridTestHelper.GetDisplayedValues(grid, p => p.Name);
        Assert.That(values, Is.EqualTo(new[] { "Alice", "Bot1", "Charlie", "Diana", "Eve" }));
    }
}
