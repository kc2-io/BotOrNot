using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class LibraryOpponentFilterTests
{
    private readonly List<string> _settingsPaths = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var path in _settingsPaths)
            if (File.Exists(path)) File.Delete(path);
        _settingsPaths.Clear();
    }

    [AvaloniaTest]
    public async Task Filter_UsesStableIdAndSingleSelectionWithoutChangingSourceOrScanning()
    {
        var cache = new MutableCache(InitialReplays());
        using var vm = new LibraryViewModel(_ => { }, cache, Settings());
        vm.DirectoryPath = Path.GetTempPath();
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => cache.ScanCount == 1 && vm.Replays.Count == 3 && !vm.IsScanning);

        var choices = vm.FrequentOpponents.Where(x => x.Name == "Shared name").ToArray();
        Assert.That(choices, Has.Length.EqualTo(2));
        var a = choices.Single(x => x.StableId.Equals("a", StringComparison.OrdinalIgnoreCase));
        var b = choices.Single(x => x.StableId.Equals("b", StringComparison.OrdinalIgnoreCase));
        vm.ToggleOpponentFilter(a);
        Assert.Multiple(() =>
        {
            Assert.That(vm.VisibleReplays.Select(x => x.FileName), Is.EqualTo(new[] { "a.replay", "both.replay" }));
            Assert.That(vm.Replays, Has.Count.EqualTo(3));
            Assert.That(vm.TotalMatches, Is.EqualTo(2));
            Assert.That(vm.TotalWins, Is.EqualTo(1));
            Assert.That(vm.AvgKills, Is.EqualTo(5));
            Assert.That(vm.FilterSummaryText, Is.EqualTo("Showing 2 of 3 matches with Shared name"));
            Assert.That(vm.SelectedReplayCount, Is.EqualTo(3), "Scan counters remain the full-library counts.");
            Assert.That(vm.FrequentOpponents, Has.Count.EqualTo(2));
            Assert.That(vm.FrequentOpponents.Single(x => x.IsSelected).StableId,
                Is.EqualTo(a.StableId).IgnoreCase);
            Assert.That(cache.ScanCount, Is.EqualTo(1));
        });

        vm.ToggleOpponentFilter(b);
        Assert.Multiple(() =>
        {
            Assert.That(vm.VisibleReplays.Select(x => x.FileName), Is.EqualTo(new[] { "b.replay", "both.replay" }));
            Assert.That(vm.TotalWins, Is.Zero);
            Assert.That(vm.FrequentOpponents.Count(x => x.IsSelected), Is.EqualTo(1));
            Assert.That(vm.FrequentOpponents.Single(x => x.IsSelected).StableId, Is.EqualTo("b").IgnoreCase);
        });
        vm.ToggleOpponentFilter(b);
        Assert.Multiple(() =>
        {
            Assert.That(vm.SelectedOpponentStableId, Is.Null);
            Assert.That(vm.VisibleReplays, Has.Count.EqualTo(3));
            Assert.That(vm.TotalMatches, Is.EqualTo(3));
            Assert.That(cache.ScanCount, Is.EqualTo(1));
        });
    }

    [AvaloniaTest]
    public async Task SameFolderRefresh_PreservesSelectionThroughCachedAndLoadedRowsAndMissingResult()
    {
        var cache = new MutableCache(InitialReplays());
        using var vm = new LibraryViewModel(_ => { }, cache, Settings());
        vm.DirectoryPath = Path.GetTempPath();
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => vm.Replays.Count == 3 && !vm.IsScanning);
        vm.ToggleOpponentFilter(vm.FrequentOpponents.Single(x => x.StableId.Equals("a", StringComparison.OrdinalIgnoreCase)));

        cache.Summaries =
        [
            Summary("fresh-a.replay", "a", "New name", 1, 8),
            Summary("fresh-b.replay", "b", "Shared name", 2, 2)
        ];
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => cache.ScanCount == 2 && vm.Replays.Count == 2 && !vm.IsScanning);
        Assert.Multiple(() =>
        {
            Assert.That(vm.VisibleReplays.Select(x => x.FileName), Is.EqualTo(new[] { "fresh-a.replay" }));
            Assert.That(vm.Replays, Has.Count.EqualTo(2));
            Assert.That(vm.SelectedOpponentStableId, Is.EqualTo("A").IgnoreCase);
            Assert.That(vm.SelectedOpponentName, Is.EqualTo("New name"));
            Assert.That(vm.FilterSummaryText, Is.EqualTo("Showing 1 of 2 matches with New name"));
            Assert.That(vm.FrequentOpponents, Has.Count.EqualTo(2));
        });

        cache.Summaries = [Summary("only-b.replay", "b", "Shared name", 2, 2)];
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => cache.ScanCount == 3 && vm.Replays.Count == 1 && !vm.IsScanning);
        Assert.Multiple(() =>
        {
            Assert.That(vm.VisibleReplays, Is.Empty);
            Assert.That(vm.HasNoFilteredMatches, Is.True);
            Assert.That(vm.FilterSummaryText, Is.EqualTo("Showing 0 of 1 matches with New name"));
            Assert.That(vm.SelectedOpponentStableId, Is.EqualTo("A").IgnoreCase);
            Assert.That(vm.FrequentOpponents.Single().StableId, Is.EqualTo("b"));
        });
        vm.ClearOpponentFilterCommand.Execute().Subscribe();
        Assert.That(vm.VisibleReplays.Select(x => x.FileName), Is.EqualTo(new[] { "only-b.replay" }));

        vm.ToggleOpponentFilter(vm.FrequentOpponents.Single());
        vm.DirectoryPath = Path.Combine(Path.GetTempPath(), "new-folder");
        Assert.Multiple(() =>
        {
            Assert.That(vm.SelectedOpponentStableId, Is.Null);
            Assert.That(vm.HasOpponentFilter, Is.False);
            Assert.That(vm.Replays, Is.Empty);
        });
    }

    [AvaloniaTest]
    public async Task AutomaticRefresh_UsesCurrentFilterAndKeepsFullSource()
    {
        var clock = new ManualTimeProvider();
        var cache = new MutableCache(InitialReplays());
        using var vm = new LibraryViewModel(_ => { }, cache, Settings(), timeProvider: clock);
        vm.DirectoryPath = Path.GetTempPath();
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => vm.Replays.Count == 3 && !vm.IsScanning);
        vm.ToggleOpponentFilter(vm.FrequentOpponents.Single(x => x.StableId.Equals("a", StringComparison.OrdinalIgnoreCase)));

        cache.Summaries =
        [
            Summary("new-b.replay", "b", "Shared name", 2, 1),
            Summary("new-a.replay", "a", "Shared name", 1, 4)
        ];
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForAsync(() => cache.ScanCount == 2 && vm.Replays.Count == 2 && !vm.IsScanning);
        Assert.Multiple(() =>
        {
            Assert.That(vm.Replays, Has.Count.EqualTo(2));
            Assert.That(vm.VisibleReplays.Select(x => x.FileName), Is.EqualTo(new[] { "new-a.replay" }));
            Assert.That(vm.SelectedOpponentStableId, Is.EqualTo("A").IgnoreCase);
        });
    }

    [AvaloniaTest]
    public async Task RenderedOpponentChip_ClickFiltersAndClearRestoresGridWithoutScan()
    {
        var cache = new MutableCache(InitialReplays());
        using var vm = new LibraryViewModel(_ => { }, cache, Settings());
        vm.DirectoryPath = Path.GetTempPath();
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => vm.Replays.Count == 3 && !vm.IsScanning);
        var view = new LibraryView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            Render(window);
            var chip = view.GetVisualDescendants().OfType<ToggleButton>()
                .Single(x => x.Name == "OpponentFilterChip" &&
                    (x.DataContext as FrequentOpponent)?.StableId.Equals("a", StringComparison.OrdinalIgnoreCase) == true);
            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(window);
            var selectedChip = view.GetVisualDescendants().OfType<ToggleButton>()
                .Single(x => x.Name == "OpponentFilterChip" &&
                    (x.DataContext as FrequentOpponent)?.StableId.Equals("a", StringComparison.OrdinalIgnoreCase) == true);
            var grid = view.FindControl<DataGrid>("ReplayGrid")!;
            Assert.Multiple(() =>
            {
                Assert.That(selectedChip.IsChecked, Is.True);
                Assert.That(grid.ItemsSource, Is.SameAs(vm.VisibleReplays));
                Assert.That(grid.ItemsSource!.Cast<ReplaySummary>().Count(), Is.EqualTo(2));
                Assert.That(vm.FilterSummaryText, Is.EqualTo("Showing 2 of 3 matches with Shared name"));
                Assert.That(cache.ScanCount, Is.EqualTo(1));
            });

            var clear = view.FindControl<Button>("ClearOpponentFilterButton")!;
            Assert.That(clear.Command, Is.Not.Null);
            clear.Command!.Execute(null);
            Render(window);
            Assert.That(grid.ItemsSource!.Cast<ReplaySummary>().Count(), Is.EqualTo(3));
            Assert.That(cache.ScanCount, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    private ISettingsService Settings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"botornot-opponent-filter-{Guid.NewGuid():N}.json");
        _settingsPaths.Add(path);
        return new SettingsService(path);
    }

    private static ReplaySummary[] InitialReplays() =>
    [
        Summary("a.replay", "A", "Shared name", 1, 6),
        Summary("b.replay", "b", "Shared name", 2, 2),
        new ReplaySummary
        {
            FileName = "both.replay", FilePath = Path.Combine(Path.GetTempPath(), "both.replay"),
            FileDate = DateTime.UtcNow.AddMinutes(-2), Placement = "2", Kills = 4,
            OpponentAnalysisComplete = true,
            Opponents = [new OpponentSummary { StableId = "a", Name = "Shared name" },
                new OpponentSummary { StableId = "B", Name = "Shared name" }]
        }
    ];

    private static ReplaySummary Summary(string file, string id, string name, int place, int kills) => new()
    {
        FileName = file,
        FilePath = Path.Combine(Path.GetTempPath(), file),
        FileDate = DateTime.UtcNow.AddMinutes(-Array.IndexOf(new[] { "a.replay", "b.replay", "both.replay" }, file)),
        Placement = place.ToString(), Kills = kills,
        OpponentAnalysisComplete = true,
        Opponents = [new OpponentSummary { StableId = id, Name = name }]
    };

    private static ReplayScanUpdate Update(ReplayScanStatus status, int processed, int selected,
        ReplaySummary? summary = null) => new()
    {
        ScanId = Guid.NewGuid(), Status = status, ConfiguredLimit = 50,
        AvailableCount = selected, SelectedCount = selected,
        ProcessedCount = processed, LoadedCount = processed, FailedCount = 0, Summary = summary
    };

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.Fail("Timed out waiting for the library scan.");
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class MutableCache(IReadOnlyList<ReplaySummary> initial) : IReplayCacheService
    {
        public IReadOnlyList<ReplaySummary> Summaries { get; set; } = initial;
        public int ScanCount { get; private set; }
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ScanCount++;
            var current = Summaries.ToArray();
            yield return Update(ReplayScanStatus.Started, 0, current.Length);
            for (var i = 0; i < current.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Update(i == 0 ? ReplayScanStatus.Cached : ReplayScanStatus.Loaded,
                    i + 1, current.Length, current[i]);
                await Task.Yield();
            }
            yield return Update(ReplayScanStatus.Completed, current.Length, current.Length);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _ticks + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan by)
        {
            _ticks += by.Ticks;
            foreach (var timer in _timers.Where(t => !t.Disposed && t.DueTicks <= _ticks).ToArray())
            {
                timer.Dispose();
                timer.Fire();
            }
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, long dueTicks) : ITimer
        {
            public long DueTicks { get; } = dueTicks;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Fire() => callback(state);
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
