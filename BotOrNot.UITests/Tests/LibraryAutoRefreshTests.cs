using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class LibraryAutoRefreshTests
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
    public void RenderedControls_UpdateAndPersistAutoRefreshPreferences()
    {
        var settings = Settings();
        using var vm = new LibraryViewModel(_ => { }, new RecordingCache(), settings);
        var view = new LibraryView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 700 };
        try
        {
            window.Show();
            Render(window);
            var checkbox = view.FindControl<CheckBox>("AutoRefreshEnabledCheckBox")!;
            var input = view.FindControl<TextBox>("AutoRefreshMinutesInput")!;
            var increase = view.FindControl<Button>("IncreaseAutoRefreshMinutesButton")!;
            var decrease = view.FindControl<Button>("DecreaseAutoRefreshMinutesButton")!;
            var apply = view.FindControl<Button>("ApplyAutoRefreshMinutesButton")!;

            Assert.That(checkbox.IsChecked, Is.True);
            Assert.That(input.Text, Is.EqualTo("10"));
            Assert.That(increase.Command, Is.Not.Null);
            increase.Command!.Execute(null);
            Render(window);
            Assert.That(input.Text, Is.EqualTo("11"));
            Assert.That(decrease.Command, Is.Not.Null);
            decrease.Command!.Execute(null);
            Render(window);
            Assert.That(input.Text, Is.EqualTo("10"));

            input.Text = "23";
            Render(window);
            Assert.That(apply.Command, Is.Not.Null);
            apply.Command!.Execute(null);
            Assert.That(settings.Load().LibraryAutoRefreshMinutes, Is.EqualTo(23));

            input.Text = "24";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.That(settings.Load().LibraryAutoRefreshMinutes, Is.EqualTo(24));

            checkbox.IsChecked = false;
            Render(window);
            Assert.That(settings.Load().LibraryAutoRefreshEnabled, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task AppNavigation_PausesAndRestartsLibraryRefreshCountdown()
    {
        var clock = new ManualTimeProvider();
        var cache = new RecordingCache();
        using var app = new AppViewModel(Settings(), cacheService: cache, timeProvider: clock);
        app.LibraryPage.DirectoryPath = Path.GetTempPath();
        app.LibraryPage.OpenReplayCommand.Execute(Summary("missing.replay")).Subscribe();
        Assert.That(app.CurrentPage, Is.TypeOf<MainWindowViewModel>());
        Assert.That(clock.NextDue, Is.Null);

        clock.Advance(TimeSpan.FromMinutes(10));
        await DrainAsync();
        Assert.That(cache.ScanCount, Is.Zero);
        ((MainWindowViewModel)app.CurrentPage).BackCommand.Execute().Subscribe();
        Assert.That(clock.NextDue, Is.EqualTo(TimeSpan.FromMinutes(20)));
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForAsync(() => cache.ScanCount == 1);
    }

    [AvaloniaTest]
    public async Task DefaultDueScan_UsesCurrentDirectoryAndLimit()
    {
        var clock = new ManualTimeProvider();
        var cache = new RecordingCache();
        var settings = Settings();
        settings.Save(new AppSettings { ReplayScanLimit = 7 });
        using var vm = new LibraryViewModel(_ => { }, cache, settings, timeProvider: clock);
        vm.DirectoryPath = Path.GetTempPath();

        Assert.Multiple(() =>
        {
            Assert.That(vm.AutoRefreshEnabled, Is.True);
            Assert.That(vm.AutoRefreshMinutes, Is.EqualTo(10));
            Assert.That(clock.NextDue, Is.EqualTo(TimeSpan.FromMinutes(10)));
        });
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.That(cache.ScanCount, Is.Zero);
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => cache.ScanCount == 1 && !vm.IsScanning);
        Assert.Multiple(() =>
        {
            Assert.That(cache.LastDirectory, Is.EqualTo(Path.GetTempPath()));
            Assert.That(cache.LastLimit, Is.EqualTo(7));
            Assert.That(clock.NextDue, Is.EqualTo(TimeSpan.FromMinutes(20)));
        });
    }

    [AvaloniaTest]
    public async Task IntervalEditing_ValidatesPersistsAndRestartsSchedule()
    {
        var clock = new ManualTimeProvider();
        var cache = new RecordingCache();
        var settings = Settings();
        using var vm = new LibraryViewModel(_ => { }, cache, settings, timeProvider: clock);
        vm.DirectoryPath = Path.GetTempPath();

        foreach (var invalid in new[] { "0", "-1", "1.5", "abc", "1441", "2147483648", "" })
        {
            vm.AutoRefreshMinutesText = invalid;
            vm.ApplyAutoRefreshMinutesCommand.Execute().Subscribe();
            Assert.That(vm.AutoRefreshMinutes, Is.EqualTo(10), invalid);
            Assert.That(settings.Load().LibraryAutoRefreshMinutes, Is.EqualTo(10), invalid);
        }

        vm.IncreaseAutoRefreshMinutesCommand.Execute().Subscribe();
        Assert.That(vm.AutoRefreshMinutes, Is.EqualTo(11));
        vm.DecreaseAutoRefreshMinutesCommand.Execute().Subscribe();
        Assert.That(vm.AutoRefreshMinutes, Is.EqualTo(10));
        vm.AutoRefreshMinutesText = "1";
        vm.ApplyAutoRefreshMinutesCommand.Execute().Subscribe();
        Assert.That(settings.Load().LibraryAutoRefreshMinutes, Is.EqualTo(1));
        vm.AutoRefreshMinutesText = "1440";
        vm.ApplyAutoRefreshMinutesCommand.Execute().Subscribe();
        Assert.That(settings.Load().LibraryAutoRefreshMinutes, Is.EqualTo(1440));
        var replacedTimer = clock.LastTimer!;
        vm.AutoRefreshMinutesText = "3";
        vm.ApplyAutoRefreshMinutesCommand.Execute().Subscribe();
        Assert.That(settings.Load().LibraryAutoRefreshMinutes, Is.EqualTo(3));
        Assert.That(clock.NextDue, Is.EqualTo(TimeSpan.FromMinutes(3)));
        replacedTimer.FireEvenIfDisposed();
        await DrainAsync();
        Assert.That(cache.ScanCount, Is.Zero, "The replaced timer must not run at the old interval.");

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That(cache.ScanCount, Is.Zero);
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => cache.ScanCount == 1);
    }

    [AvaloniaTest]
    public async Task DisableNavigationAndDispose_StopRefreshAndIgnoreStaleCallbacks()
    {
        var clock = new ManualTimeProvider();
        var cache = new RecordingCache();
        var settings = Settings();
        var vm = new LibraryViewModel(_ => { }, cache, settings, timeProvider: clock);
        vm.DirectoryPath = Path.GetTempPath();
        var stale = clock.LastTimer!;

        vm.AutoRefreshEnabled = false;
        stale.FireEvenIfDisposed();
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();
        Assert.Multiple(() =>
        {
            Assert.That(cache.ScanCount, Is.Zero);
            Assert.That(settings.Load().LibraryAutoRefreshEnabled, Is.False);
            Assert.That(clock.NextDue, Is.Null);
        });

        vm.AutoRefreshEnabled = true;
        vm.SetActive(false);
        clock.Advance(TimeSpan.FromMinutes(10));
        await DrainAsync();
        Assert.That(cache.ScanCount, Is.Zero);
        vm.SetActive(true);
        Assert.That(clock.NextDue, Is.EqualTo(TimeSpan.FromMinutes(80)));
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForAsync(() => cache.ScanCount == 1);
        var beforeDispose = clock.LastTimer!;
        vm.Dispose();
        beforeDispose.FireEvenIfDisposed();
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();
        Assert.That(cache.ScanCount, Is.EqualTo(1));
        Assert.That(clock.NextDue, Is.Null);
    }

    [AvaloniaTest]
    public async Task DueTickDuringManualScan_DoesNotCancelOrQueueAnotherScan()
    {
        var clock = new ManualTimeProvider();
        var cache = new HoldingFirstScanCache();
        using var vm = new LibraryViewModel(_ => { }, cache, Settings(), timeProvider: clock);
        vm.DirectoryPath = Path.GetTempPath();
        vm.ScanCommand.Execute().Subscribe();
        await cache.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMinutes(10));
        await DrainAsync();
        Assert.That(cache.ScanCount, Is.EqualTo(1));
        Assert.That(cache.FirstWasCancelled, Is.False);

        cache.ReleaseFirst.TrySetResult();
        await WaitForAsync(() => !vm.IsScanning);
        Assert.That(cache.ScanCount, Is.EqualTo(1), "A skipped tick must not queue a scan.");
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForAsync(() => cache.ScanCount == 2);
    }

    [AvaloniaTest]
    public async Task DueTick_SkipsCancelledScanStillDrainingAfterReplacementCompletes()
    {
        var clock = new ManualTimeProvider();
        var cache = new SlowCancelledScanCache();
        using var vm = new LibraryViewModel(_ => { }, cache, Settings(), timeProvider: clock);
        vm.DirectoryPath = Path.GetTempPath();
        vm.ScanCommand.Execute().Subscribe();
        await cache.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => cache.ScanCount == 2 && !vm.IsScanning);

        clock.Advance(TimeSpan.FromMinutes(10));
        await DrainAsync();
        Assert.That(cache.ScanCount, Is.EqualTo(2), "The canceled scan is still in flight.");

        cache.ReleaseFirst.TrySetResult();
        await cache.FirstDrained.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await DrainAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForAsync(() => cache.ScanCount == 3);
    }

    [AvaloniaTest]
    public async Task AutomaticRefresh_PreservesRowsUntilFirstResultAndClearsOnEmptyCompletion()
    {
        var clock = new ManualTimeProvider();
        var cache = new ControlledRefreshCache();
        using var vm = new LibraryViewModel(_ => { }, cache, Settings(), timeProvider: clock);
        vm.DirectoryPath = Path.GetTempPath();
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => vm.Replays.SingleOrDefault()?.FileName == "old.replay" && !vm.IsScanning);

        clock.Advance(TimeSpan.FromMinutes(10));
        await cache.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(vm.Replays.Select(x => x.FileName), Is.EqualTo(new[] { "old.replay" }));
        cache.ReleaseSecond.TrySetResult();
        await WaitForAsync(() => vm.Replays.SingleOrDefault()?.FileName == "new.replay" && !vm.IsScanning);

        clock.Advance(TimeSpan.FromMinutes(10));
        await cache.ThirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(vm.Replays.Select(x => x.FileName), Is.EqualTo(new[] { "new.replay" }));
        cache.ReleaseThird.TrySetResult();
        await WaitForAsync(() => vm.Replays.Count == 0 && !vm.IsScanning);
    }

    [AvaloniaTest]
    public async Task FailedAutomaticRefresh_LeavesLastSuccessfulRowsVisible()
    {
        var clock = new ManualTimeProvider();
        var cache = new FailingRefreshCache();
        using var vm = new LibraryViewModel(_ => { }, cache, Settings(), timeProvider: clock);
        vm.DirectoryPath = Path.GetTempPath();
        vm.ScanCommand.Execute().Subscribe();
        await WaitForAsync(() => vm.Replays.SingleOrDefault()?.FileName == "old.replay" && !vm.IsScanning);

        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForAsync(() => vm.ErrorMessage?.Contains("Scan failed") == true && !vm.IsScanning);
        Assert.That(vm.Replays.Select(x => x.FileName), Is.EqualTo(new[] { "old.replay" }));
    }

    private ISettingsService Settings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"botornot-auto-refresh-{Guid.NewGuid():N}.json");
        _settingsPaths.Add(path);
        return new SettingsService(path);
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task DrainAsync()
    {
        for (var i = 0; i < 5; i++)
            await Task.Delay(1);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.Fail("Timed out waiting for library refresh state.");
    }

    private static ReplayScanUpdate Update(ReplayScanStatus status, ReplaySummary? summary = null) => new()
    {
        ScanId = Guid.NewGuid(),
        Status = status,
        ConfiguredLimit = 50,
        Summary = summary,
        AvailableCount = summary is null ? 0 : 1,
        SelectedCount = summary is null ? 0 : 1,
        ProcessedCount = summary is null ? 0 : 1,
        LoadedCount = summary is null ? 0 : 1,
        FailedCount = 0
    };

    private static ReplaySummary Summary(string name) => new()
    {
        FileName = name,
        FilePath = Path.Combine(Path.GetTempPath(), name),
        FileDate = DateTime.UtcNow
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        public ManualTimer? LastTimer => _timers.LastOrDefault();
        public TimeSpan? NextDue => _timers.Where(t => !t.Disposed)
            .Select(t => (TimeSpan?)TimeSpan.FromTicks(t.DueTicks)).Min();

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
                timer.FireEvenIfDisposed();
            }
        }

        public sealed class ManualTimer(TimerCallback callback, object? state, long dueTicks) : ITimer
        {
            public long DueTicks { get; private set; } = dueTicks;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueTicks = dueTime.Ticks;
                return !Disposed;
            }
            public void FireEvenIfDisposed() => callback(state);
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingCache : IReplayCacheService
    {
        public int ScanCount { get; private set; }
        public string? LastDirectory { get; private set; }
        public int? LastLimit { get; private set; }
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ScanCount++;
            LastDirectory = directory;
            LastLimit = options?.Limit;
            yield return Update(ReplayScanStatus.Started);
            yield return Update(ReplayScanStatus.Completed);
            await Task.CompletedTask;
        }
    }

    private sealed class HoldingFirstScanCache : IReplayCacheService
    {
        public int ScanCount { get; private set; }
        public bool FirstWasCancelled { get; private set; }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = ++ScanCount;
            yield return Update(ReplayScanStatus.Started);
            if (index == 1)
            {
                FirstStarted.TrySetResult();
                try { await ReleaseFirst.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { FirstWasCancelled = true; throw; }
            }
            yield return Update(ReplayScanStatus.Completed);
        }
    }

    private sealed class ControlledRefreshCache : IReplayCacheService
    {
        private int _count;
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseThird { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = ++_count;
            yield return Update(ReplayScanStatus.Started);
            if (index == 2)
            {
                SecondStarted.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }
            if (index == 3)
            {
                ThirdStarted.TrySetResult();
                await ReleaseThird.Task.WaitAsync(cancellationToken);
            }
            if (index < 3)
                yield return Update(ReplayScanStatus.Cached, Summary(index == 1 ? "old.replay" : "new.replay"));
            yield return Update(ReplayScanStatus.Completed);
        }
    }

    private sealed class SlowCancelledScanCache : IReplayCacheService
    {
        public int ScanCount { get; private set; }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = ++ScanCount;
            try
            {
                yield return Update(ReplayScanStatus.Started);
                if (index == 1)
                {
                    FirstStarted.TrySetResult();
                    await ReleaseFirst.Task; // Simulates an iterator that cannot cancel its current read.
                }
                yield return Update(ReplayScanStatus.Completed);
            }
            finally
            {
                if (index == 1) FirstDrained.TrySetResult();
            }
        }
    }

    private sealed class FailingRefreshCache : IReplayCacheService
    {
        private int _count;
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory,
            IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = ++_count;
            yield return Update(ReplayScanStatus.Started);
            if (index == 1)
            {
                yield return Update(ReplayScanStatus.Cached, Summary("old.replay"));
                yield return Update(ReplayScanStatus.Completed);
            }
            else
            {
                await Task.Yield();
                throw new IOException("simulated refresh failure");
            }
        }
    }
}
