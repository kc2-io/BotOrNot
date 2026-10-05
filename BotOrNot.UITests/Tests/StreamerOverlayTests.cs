using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class StreamerOverlayTests
{
    private string _settingsPath = null!;

    [SetUp]
    public void SetUp() => _settingsPath = Path.Combine(Path.GetTempPath(), $"botornot-overlay-{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() => File.Delete(_settingsPath);

    [AvaloniaTest]
    public async Task Controls_EnableDisableAndApplySavedPort()
    {
        var settings = new SettingsService(_settingsPath);
        var server = new RecordingOverlayServer();
        using var library = new LibraryViewModel(_ => { }, settingsService: settings, overlayServer: server);
        var view = new LibraryView { DataContext = library };
        var window = new Window { Content = view, Width = 1300, Height = 700 };
        try
        {
            window.Show();
            Render(window);
            var toggle = view.FindControl<Button>("ToggleStreamerOverlayButton")!;
            var input = view.FindControl<TextBox>("StreamerOverlayPortInput")!;
            var copy = view.FindControl<Button>("CopyStreamerOverlayUrlButton")!;
            var preview = view.FindControl<Button>("PreviewStreamerOverlayButton")!;
            Assert.That(copy.IsEnabled, Is.False);
            input.Text = "19000";
            Render(window);
            await library.StreamerOverlay.ApplyPortCommand.Execute().FirstAsync();
            await library.StreamerOverlay.ToggleCommand.Execute().FirstAsync();
            Render(window);
            Assert.Multiple(() =>
            {
                Assert.That(server.Port, Is.EqualTo(19000));
                Assert.That(settings.Load().StreamerOverlayEnabled, Is.True);
                Assert.That(settings.Load().StreamerOverlayPort, Is.EqualTo(19000));
                Assert.That(toggle.Content, Is.EqualTo("Disable browser overlay"));
                Assert.That(input.IsEnabled, Is.False);
                Assert.That(copy.IsEnabled, Is.True);
                Assert.That(preview.IsEnabled, Is.True);
                Assert.That(library.StreamerOverlay.Url, Is.EqualTo("http://127.0.0.1:19000/"));
            });
            await library.StreamerOverlay.ToggleCommand.Execute().FirstAsync();
            Render(window);
            Assert.That(server.IsRunning, Is.False);
            Assert.That(settings.Load().StreamerOverlayEnabled, Is.False);
            Assert.That(copy.IsEnabled, Is.False);
            input.Text = "0";
            Render(window);
            await library.StreamerOverlay.ApplyPortCommand.Execute().FirstAsync();
            Assert.That(settings.Load().StreamerOverlayPort, Is.EqualTo(19000));
            Assert.That(library.StreamerOverlay.Message, Does.Contain("1024"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task SavedEnabledPreference_RestoresListener_AndStartupConflictCanRetry()
    {
        var settings = new SettingsService(_settingsPath);
        settings.Save(new AppSettings { StreamerOverlayEnabled = true, StreamerOverlayPort = 19000 });
        var server = new RecordingOverlayServer { FailStart = true };
        using var library = new LibraryViewModel(_ => { }, settingsService: settings, overlayServer: server);
        await library.StreamerOverlay.Initialization;
        Assert.That(library.StreamerOverlay.IsEnabled, Is.False);
        Assert.That(library.StreamerOverlay.IsBusy, Is.False);
        Assert.That(settings.Load().StreamerOverlayEnabled, Is.False);
        Assert.That(library.StreamerOverlay.Message, Does.Contain("19000"));
        server.FailStart = false;
        await library.StreamerOverlay.ToggleCommand.Execute().FirstAsync();
        library.Dispose();
        await library.StreamerOverlay.Shutdown;
        Assert.That(server.IsRunning, Is.False);
        Assert.That(settings.Load().StreamerOverlayEnabled, Is.True, "Shutdown preserves the launch preference.");
        using var restored = new LibraryViewModel(_ => { }, settingsService: settings, overlayServer: server);
        await restored.StreamerOverlay.Initialization;
        Assert.That(restored.StreamerOverlay.IsEnabled, Is.True);
        restored.Dispose();
        await restored.StreamerOverlay.Shutdown;
    }

    [AvaloniaTest]
    public async Task DisposeDuringStart_StopsLateListenerWithoutReactivatingRefresh()
    {
        var settings = new SettingsService(_settingsPath);
        settings.Save(new AppSettings { StreamerOverlayEnabled = true });
        var server = new RecordingOverlayServer { StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var active = false;
        var vm = new StreamerOverlayViewModel(settings, new(), value => active = value, server);
        Assert.That(vm.IsBusy, Is.True);
        vm.Dispose();
        server.StartGate.SetResult();
        await vm.Initialization;
        await vm.Shutdown;
        Assert.That(active, Is.False);
        Assert.That(server.IsRunning, Is.False);
    }

    [AvaloniaTest]
    public async Task Snapshot_CommitsOnlyCompletedUnfilteredScan_AndRetainsResultsOnFailure()
    {
        var cache = new ControlledCache();
        using var library = new LibraryViewModel(_ => { }, cache, new SettingsService(_settingsPath),
            overlayServer: new RecordingOverlayServer()) { DirectoryPath = Path.GetTempPath() };
        library.ScanCommand.Execute().Subscribe();
        await WaitFor(() => library.OverlayState.Current.Snapshot is not null);
        var original = library.OverlayState.Current.Snapshot!;
        Assert.That(original.Statistics.Matches, Is.EqualTo(2));
        library.ToggleOpponentFilter(library.FrequentOpponents.Single());
        Assert.That(library.TotalMatches, Is.EqualTo(1));
        Assert.That(library.OverlayState.Current.Snapshot!.Statistics.Matches, Is.EqualTo(2));

        cache.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        library.ScanCommand.Execute().Subscribe();
        await WaitFor(() => library.Replays.Count == 1 && library.IsScanning);
        Assert.That(library.OverlayState.Current.Snapshot, Is.SameAs(original));
        cache.Gate.SetResult();
        await WaitFor(() => !ReferenceEquals(library.OverlayState.Current.Snapshot, original));
        Assert.That(library.OverlayState.Current.Snapshot!.Statistics.Matches, Is.EqualTo(2));

        var last = library.OverlayState.Current.Snapshot;
        cache.Fail = true;
        library.ScanCommand.Execute().Subscribe();
        await WaitFor(() => library.OverlayState.Current.UpdatesDelayed);
        Assert.That(library.OverlayState.Current.Snapshot, Is.SameAs(last));
        library.DirectoryPath = Path.Combine(Path.GetTempPath(), "different-library");
        Assert.That(library.OverlayState.Current.Snapshot, Is.Null);
        Assert.That(library.OverlayState.Current.IsScanning, Is.False);
    }

    public sealed class RecordingOverlayServer : IStreamerOverlayServer
    {
        public int Port { get; private set; }
        public bool IsRunning { get; private set; }
        public bool FailStart { get; set; }
        public TaskCompletionSource? StartGate { get; set; }
        public async Task StartAsync(int port, StreamerOverlayState state)
        {
            if (FailStart) throw new IOException("Port occupied");
            if (StartGate is not null) await StartGate.Task;
            Port = port;
            IsRunning = true;
        }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => new(StopAsync());
    }

    private sealed class ControlledCache : IReplayCacheService
    {
        public TaskCompletionSource? Gate { get; set; }
        public bool Fail { get; set; }
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory, IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(string directory, ReplayScanOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Unavailable directory");
            var scanId = Guid.NewGuid();
            ReplayScanUpdate Update(ReplayScanStatus status, int loaded, ReplaySummary? summary = null) => new()
            {
                ScanId = scanId, Status = status, ConfiguredLimit = options?.Limit ?? 50,
                AvailableCount = 2, SelectedCount = 2, ProcessedCount = loaded, LoadedCount = loaded,
                FailedCount = 0, Summary = summary
            };
            yield return Update(ReplayScanStatus.Started, 0);
            yield return Update(ReplayScanStatus.Loaded, 1, new()
            {
                FilePath = "/first.replay", FileDate = DateTime.UtcNow, Kills = 4,
                Opponents = [new() { StableId = "opponent", Name = "Opponent" }]
            });
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            yield return Update(ReplayScanStatus.Loaded, 2, new()
            {
                FilePath = "/second.replay", FileDate = DateTime.UtcNow.AddMinutes(-1), Kills = null
            });
            yield return Update(ReplayScanStatus.Completed, 2);
        }
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.Fail("Timed out waiting for overlay state.");
    }
}
