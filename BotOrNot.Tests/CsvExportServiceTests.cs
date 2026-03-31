using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class CsvExportServiceTests
{
    private static readonly List<CsvColumnDefinition> TestColumns = new()
    {
        new("Name", p => p.Name ?? ""),
        new("Kills", p => p.Kills ?? "0"),
        new("Bot", p => p.Bot == "true" ? "Yes" : "No"),
    };

    [Test]
    public void GenerateCsv_EmptyRows_ReturnsHeaderOnly()
    {
        var csv = CsvExportService.GenerateCsv([], TestColumns);
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.That(lines, Has.Length.EqualTo(1));
        Assert.That(lines[0], Is.EqualTo("Name,Kills,Bot"));
    }

    [Test]
    public void GenerateCsv_SingleRow_ReturnsHeaderAndData()
    {
        var rows = new[] { new PlayerRow { Name = "Player1", Kills = "5", Bot = "false" } };
        var csv = CsvExportService.GenerateCsv(rows, TestColumns);
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(lines[0], Is.EqualTo("Name,Kills,Bot"));
        Assert.That(lines[1], Is.EqualTo("Player1,5,No"));
    }

    [Test]
    public void GenerateCsv_MultipleRows_AllPresent()
    {
        var rows = new[]
        {
            new PlayerRow { Name = "Alice", Kills = "3", Bot = "false" },
            new PlayerRow { Name = "Bob", Kills = "0", Bot = "true" },
        };
        var csv = CsvExportService.GenerateCsv(rows, TestColumns);
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.That(lines, Has.Length.EqualTo(3));
        Assert.That(lines[1], Is.EqualTo("Alice,3,No"));
        Assert.That(lines[2], Is.EqualTo("Bob,0,Yes"));
    }

    [Test]
    public void GenerateCsv_EscapesCommasInValues()
    {
        var columns = new List<CsvColumnDefinition>
        {
            new("Name", p => p.Name ?? ""),
            new("Cause", p => p.DeathCause ?? ""),
        };
        var rows = new[] { new PlayerRow { Name = "Test", DeathCause = "Fell, then eliminated" } };
        var csv = CsvExportService.GenerateCsv(rows, columns);
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.That(lines[1], Is.EqualTo("Test,\"Fell, then eliminated\""));
    }

    [Test]
    public void GenerateCsv_EscapesQuotesInValues()
    {
        var columns = new List<CsvColumnDefinition>
        {
            new("Name", p => p.Name ?? ""),
        };
        var rows = new[] { new PlayerRow { Name = "Player \"Pro\"" } };
        var csv = CsvExportService.GenerateCsv(rows, columns);
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.That(lines[1], Is.EqualTo("\"Player \"\"Pro\"\"\""));
    }

    [Test]
    public void GenerateCsv_NullValues_HandledGracefully()
    {
        var rows = new[] { new PlayerRow { Name = null, Kills = null, Bot = null } };
        var csv = CsvExportService.GenerateCsv(rows, TestColumns);
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.That(lines[1], Is.EqualTo(",0,No"));
    }

    [Test]
    public void GenerateCsv_OnlySelectedColumns_Appear()
    {
        var singleColumn = new List<CsvColumnDefinition>
        {
            new("Name", p => p.Name ?? ""),
        };
        var rows = new[] { new PlayerRow { Name = "Alice", Kills = "5", Bot = "true" } };
        var csv = CsvExportService.GenerateCsv(rows, singleColumn);
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.That(lines[0], Is.EqualTo("Name"));
        Assert.That(lines[1], Is.EqualTo("Alice"));
    }
}
