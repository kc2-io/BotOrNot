using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class LibraryViewBindingTests
{
    [TestCase(true, TestName = "DateColumn_PreservesPreciseUtcValue_ForRowsPresentAtInitialBinding")]
    [TestCase(false, TestName = "DateColumn_PreservesPreciseUtcValue_ForRowsAddedAfterInitialRender")]
    [AvaloniaTest]
    public void DateColumn_PreservesPreciseUtcValueAfterGridRealizesRow(bool addBeforeBinding)
    {
        var exactDate = new DateTime(638_500_000_123_456_789, DateTimeKind.Utc);
        var exactDuration = 21.987654321;
        var summary = new ReplaySummary
        {
            FileName = "replay.replay",
            FilePath = Path.Combine(Path.GetTempPath(), "replay.replay"),
            FileDate = exactDate,
            DurationMinutes = exactDuration
        };
        using var viewModel = new LibraryViewModel(
            _ => { }, settingsService: new SettingsService(NewSettingsPath()));

        if (addBeforeBinding)
            viewModel.Replays.Add(summary);

        var view = new LibraryView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1200, Height = 700 };
        try
        {
            window.Show();
            Render(window);

            if (!addBeforeBinding)
            {
                viewModel.Replays.Add(summary);
                Render(window);
            }

            var grid = view.FindControl<DataGrid>("ReplayGrid");
            Assert.That(grid, Is.Not.Null);
            Assert.That(grid!.GetVisualDescendants().OfType<DataGridRow>(), Is.Not.Empty,
                "The assertion must run after the date cell has been bound and realized.");
            Assert.Multiple(() =>
            {
                Assert.That(summary.FileDate, Is.EqualTo(exactDate));
                Assert.That(summary.FileDate.Kind, Is.EqualTo(DateTimeKind.Utc));
                Assert.That(summary.FileDate.Ticks, Is.EqualTo(exactDate.Ticks));
                Assert.That(summary.DurationMinutes, Is.EqualTo(exactDuration));
            });
        }
        finally
        {
            window.Close();
        }
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static string NewSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"botornot-library-binding-{Guid.NewGuid():N}.json");
}
