using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;

namespace BotOrNot.Avalonia.Services;

/// <summary>
/// Opt-in native benchmark wiring. It is activated only by Program's --benchmark-config route;
/// normal application startup does not read or create benchmark files.
/// </summary>
internal static class NativeBenchmarkLaunch
{
    private static NativeBenchmarkRun? _current;

    public static NativeBenchmarkRun? Current => Volatile.Read(ref _current);

    public static void Configure(string configPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The native benchmark route is supported only on Windows.");

        // This is the first managed instruction in the explicit benchmark route. Configuration
        // and manifest validation are reported separately from every scan/render duration.
        var managedEntryAt = Stopwatch.GetTimestamp();
        var managedEntryUtc = DateTimeOffset.UtcNow;
        DateTimeOffset? processStartedUtc = null;
        try { processStartedUtc = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime()); }
        catch (InvalidOperationException) { }
        var config = NativeBenchmarkConfig.Load(configPath);
        var configValidatedAt = Stopwatch.GetTimestamp();
        var run = new NativeBenchmarkRun(config, managedEntryAt, managedEntryUtc, processStartedUtc, configValidatedAt);
        if (Interlocked.CompareExchange(ref _current, run, null) is not null)
            throw new InvalidOperationException("A native benchmark configuration is already active.");
    }
}

internal sealed class NativeBenchmarkRun : ILibraryScanObserver
{
    private readonly NativeBenchmarkConfig _config;
    private readonly long _managedEntryAt;
    private readonly long _configValidatedAt;
    private readonly DateTimeOffset _managedEntryUtc;
    private readonly DateTimeOffset? _processStartedUtc;
    private readonly ConcurrentDictionary<string, long> _timestamps = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _windowOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _windowLaidOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completionStarted;
    private int _scanDrained;
    private MainWindow? _window;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private LibraryViewModel? _library;
    private NativeProcessSample? _processAtAttach;
    private NativeCacheState? _cacheBefore;

    internal NativeBenchmarkRun(NativeBenchmarkConfig config, long managedEntryAt, DateTimeOffset managedEntryUtc,
        DateTimeOffset? processStartedUtc, long configValidatedAt)
    {
        _config = config;
        _managedEntryAt = managedEntryAt;
        _managedEntryUtc = managedEntryUtc;
        _processStartedUtc = processStartedUtc;
        _configValidatedAt = configValidatedAt;
    }

    public NativeBenchmarkConfig Config => _config;

    public void Attach(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, LibraryViewModel library)
    {
        _window = window;
        _desktop = desktop;
        _library = library;
        _cacheBefore = NativeCacheState.Read(_config.CachePath);
        _processAtAttach = NativeProcessSample.Capture();
        Mark("attached");
        window.Opened += (_, _) =>
        {
            Mark("windowOpened");
            _windowOpened.TrySetResult();
        };
        window.LayoutUpdated += (_, _) =>
        {
            if (window.ClientSize.Width > 0 && window.ClientSize.Height > 0)
            {
                Mark("windowLaidOut");
                _windowLaidOut.TrySetResult();
            }
        };

        // A terminal stream fault may never emit ScanDrained. Start this from native attachment,
        // rather than after the stream's terminal notification, so it can always emit a result.
        _ = WatchdogAsync();
        TryStartCompletion();
    }

    private async Task WatchdogAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.CompletionTimeoutSeconds)).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _completionStarted, 1, 0) == 0)
                await FinishFailureAsync("Timed out waiting for ScanDrained, native layout, or final rendering.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (Interlocked.CompareExchange(ref _completionStarted, 1, 0) == 0)
                await FinishFailureAsync($"Native benchmark watchdog failed: {exception.Message}").ConfigureAwait(false);
        }
    }

    public void OnMilestone(LibraryScanMilestone milestone)
    {
        Mark(milestone switch
        {
            LibraryScanMilestone.Invoked => "scanInvoked",
            LibraryScanMilestone.FirstModelRow => "firstModelRow",
            LibraryScanMilestone.FinalModelState => "finalModelState",
            LibraryScanMilestone.ScanDrained => "scanDrained",
            _ => milestone.ToString()
        });

        if (milestone == LibraryScanMilestone.ScanDrained)
        {
            Volatile.Write(ref _scanDrained, 1);
            TryStartCompletion();
        }
    }

    private void TryStartCompletion()
    {
        if (Volatile.Read(ref _scanDrained) == 1 && _window is not null && _desktop is not null &&
            Interlocked.Exchange(ref _completionStarted, 1) == 0)
            _ = CompleteAfterNativeRenderAsync();
    }

    private async Task CompleteAfterNativeRenderAsync()
    {
        NativeSoftwareViewportCapture? softwareCapture = null;
        NativeClientViewportCapture? nativeCapture = null;
        NativeBenchmarkVerification? verification = null;
        var dwmSucceeded = false;
        string? dwmError = null;
        try
        {
            var window = _window ?? throw new InvalidOperationException("Benchmark window was not attached.");
            await Task.WhenAll(_windowOpened.Task, _windowLaidOut.Task)
                .WaitAsync(TimeSpan.FromSeconds(_config.CompletionTimeoutSeconds)).ConfigureAwait(false);

            await AcknowledgeFinalRenderAsync(window).ConfigureAwait(false);
            dwmSucceeded = NativeCompositor.TryFlush(out dwmError);
            Mark("dwmFlushReturned");
            softwareCapture = await Dispatcher.UIThread.InvokeAsync(() => CaptureSoftwareViewport(window), DispatcherPriority.Render);
            Mark("softwareViewportPngCaptured");
            nativeCapture = await Dispatcher.UIThread.InvokeAsync(() => NativeClientCapture.Capture(window, _config.NativeClientCapturePath), DispatcherPriority.Render);
            Mark("nativeClientCaptureCompleted");
            verification = await Dispatcher.UIThread.InvokeAsync(() => VerifyFinalUi(window), DispatcherPriority.Render);

            var errors = verification.Errors.ToList();
            if (!dwmSucceeded)
                errors.Add(dwmError ?? "DwmFlush failed.");
            if (!softwareCapture.Succeeded)
                errors.Add(softwareCapture.Error ?? "RenderTargetBitmap diagnostic capture failed.");
            if (!nativeCapture.Succeeded)
                errors.Add(nativeCapture.Error ?? "PrintWindow client capture failed.");
            WriteResult(CreateResult(errors.Count == 0, errors.Count == 0 ? null : string.Join(" ", errors),
                dwmSucceeded, dwmError, softwareCapture, nativeCapture, verification));
        }
        catch (Exception exception)
        {
            WriteResult(CreateResult(false, exception.Message, dwmSucceeded, dwmError, softwareCapture, nativeCapture, verification));
        }
        finally
        {
            ShutdownDesktop();
        }
    }

    private async Task FinishFailureAsync(string error)
    {
        try
        {
            WriteResult(CreateResult(false, error, false, null, null, null, null));
        }
        finally
        {
            ShutdownDesktop();
        }
        await Task.CompletedTask;
    }

    private void ShutdownDesktop()
    {
        if (_desktop is not null)
            Dispatcher.UIThread.Post(() => _desktop.Shutdown(), DispatcherPriority.Background);
    }

    private async Task AcknowledgeFinalRenderAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var animationFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() => window.RequestAnimationFrame(_ => animationFrame.TrySetResult()), DispatcherPriority.Render);
        await animationFrame.Task.WaitAsync(TimeSpan.FromSeconds(_config.CompletionTimeoutSeconds)).ConfigureAwait(false);
        Mark("animationFrameAcknowledged");

        await RequestCompositionRenderAsync(window).ConfigureAwait(false);
        Mark("compositionUpdateAcknowledged");
        Mark("compositionBatchRendered");
    }

    // Avalonia 11.2 exposes RequestAnimationFrame publicly, but its compositor property is
    // internal. The benchmark reads the documented composition acknowledgement by reflection
    // and fails the run if that implementation detail changes; product code never uses this.
    private async Task RequestCompositionRenderAsync(Window window)
    {
        var updateAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = await Dispatcher.UIThread.InvokeAsync<Task>(() =>
        {
            var renderer = typeof(TopLevel).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
                ?? throw new InvalidOperationException("Avalonia compositor renderer is unavailable.");
            var compositor = renderer.GetType().GetProperty("Compositor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(renderer)
                ?? throw new InvalidOperationException("Avalonia compositor is unavailable.");
            var requestUpdate = compositor.GetType().GetMethod("RequestCompositionUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Avalonia compositor update acknowledgement is unavailable.");
            requestUpdate.Invoke(compositor, [(Action)(() => updateAcknowledged.TrySetResult())]);
            var requestCommit = compositor.GetType().GetMethod("RequestCompositionBatchCommitAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Avalonia composition batch acknowledgement is unavailable.");
            var batch = requestCommit.Invoke(compositor, null)
                ?? throw new InvalidOperationException("Avalonia composition batch was not created.");
            return batch.GetType().GetProperty("Rendered", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(batch) as Task
                ?? throw new InvalidOperationException("Avalonia composition batch render acknowledgement is unavailable.");
        }, DispatcherPriority.Render);
        await updateAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(_config.CompletionTimeoutSeconds)).ConfigureAwait(false);
        await rendered.WaitAsync(TimeSpan.FromSeconds(_config.CompletionTimeoutSeconds)).ConfigureAwait(false);
    }

    private NativeSoftwareViewportCapture CaptureSoftwareViewport(Window window)
    {
        try
        {
            var width = Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width * window.RenderScaling));
            var height = Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height * window.RenderScaling));
            var outputPath = Path.GetFullPath(_config.ViewportPngPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(outputPath);
            return new NativeSoftwareViewportCapture(true, null,
                "Avalonia RenderTargetBitmap diagnostic after native composition acknowledgement and DwmFlush; not a desktop capture.",
                width, height, new FileInfo(outputPath).Length);
        }
        catch (Exception exception)
        {
            return new NativeSoftwareViewportCapture(false, exception.Message, "Avalonia RenderTargetBitmap diagnostic", 0, 0, 0);
        }
    }

    private NativeBenchmarkVerification VerifyFinalUi(Window window)
    {
        var errors = new List<string>();
        var library = _library ?? throw new InvalidOperationException("Library view model was not attached.");
        var expectedSelected = Math.Min(_config.ScanLimit, _config.Manifest.Entries.Count);
        var expectedPaths = _config.Manifest.Entries.OrderBy(entry => entry.AllRank).Take(expectedSelected)
            .Select(entry => Path.GetFullPath(Path.Combine(_config.ReplayDirectory, entry.RelativePath)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPaths = library.Replays.Select(replay => Path.GetFullPath(replay.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var countersValid = !library.IsScanning && library.AvailableReplayCount == _config.Manifest.Entries.Count &&
            library.SelectedReplayCount == expectedSelected && library.ProcessedReplayCount == expectedSelected &&
            library.LoadedReplayCount == expectedSelected && library.FailedReplayCount == 0 &&
            library.Replays.Count == expectedSelected && string.IsNullOrWhiteSpace(library.ErrorMessage) &&
            actualPaths.SetEquals(expectedPaths);
        if (!countersValid)
            errors.Add("Final scan counters, errors, or selected replay identities did not match the frozen manifest.");

        var grid = window.FindControl<DataGrid>("ReplayGrid");
        var gridBound = grid?.ItemsSource is not null && grid.ItemsSource.Cast<object>().Count() == library.Replays.Count;
        var realizedRows = grid?.GetVisualDescendants().OfType<DataGridRow>().Count() ?? 0;
        if (!gridBound)
            errors.Add("Library DataGrid ItemsSource is not populated with final replay rows.");
        if (realizedRows == 0 && library.Replays.Count > 0)
            errors.Add("Library DataGrid did not realize a replay row after final layout.");

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();
        var scanStatusRendered = texts.Contains(library.ScanStatusText, StringComparer.Ordinal);
        var scanOutcomeRendered = texts.Contains(library.ScanOutcomeText, StringComparer.Ordinal);
        var totalMatchesRendered = HasLabelValuePair(window, "Matches:", library.TotalMatches.ToString(CultureInfo.InvariantCulture));
        if (!scanStatusRendered || !scanOutcomeRendered || !totalMatchesRendered)
            errors.Add("Final scan or aggregate labels were not bound into the native visual tree.");

        var projection = new NativeSummaryProjection(
            library.Replays.Select(replay => new NativeReplayProjection(replay.FilePath, replay.FileDate.ToUniversalTime(), replay.Playlist,
                replay.GameMode, replay.Placement, replay.Kills, replay.PlayerKills, replay.BotKills, replay.PlayerCount, replay.BotCount,
                replay.DurationMinutes, replay.IsWin, replay.BotPercent, replay.AnalysisStatus.ToString(), replay.OpponentAnalysisComplete)).ToArray(),
            library.TotalMatches, library.TotalWins, library.WinRate, library.AvgKills, library.AvgBotPercent,
            library.IncompleteOpponentMatchCount,
            library.FrequentOpponents.Select(opponent => new NativeOpponentProjection(opponent.StableId, opponent.Name, opponent.Appearances)).ToArray());
        return new NativeBenchmarkVerification(true, NativeManifestValidator.Fingerprint(_config.Manifest), _config.Manifest.Entries.Count,
            expectedSelected, countersValid, gridBound, realizedRows, scanStatusRendered, scanOutcomeRendered, totalMatchesRendered,
            NativeBenchmarkJson.Fingerprint(projection), errors);
    }

    private static bool HasLabelValuePair(Window window, string label, string value) =>
        window.GetVisualDescendants().OfType<StackPanel>().Any(panel =>
        {
            var values = panel.Children.OfType<TextBlock>().Select(block => block.Text ?? string.Empty).ToArray();
            return values.Contains(label, StringComparer.Ordinal) && values.Contains(value, StringComparer.Ordinal);
        });

    private NativeBenchmarkResult CreateResult(bool succeeded, string? error, bool dwmSucceeded, string? dwmError,
        NativeSoftwareViewportCapture? softwareCapture, NativeClientViewportCapture? nativeCapture,
        NativeBenchmarkVerification? verification) => new(
        NativeBenchmarkResult.CurrentSchemaVersion, succeeded, error, _config.ScanProfile, _config.ScanLimit,
        _config.MaxConcurrency, null, _config.RequireColdCache, _config.NativeVisible, _processStartedUtc, _managedEntryUtc,
        dwmSucceeded, dwmError, softwareCapture, nativeCapture, verification, SnapshotTimings(), SnapshotFinalState(),
        _cacheBefore, NativeCacheState.Read(_config.CachePath), NativeProcessMetrics.Create(_processAtAttach, NativeProcessSample.Capture()));

    private NativeBenchmarkTimings SnapshotTimings()
    {
        double? Duration(string name) => _timestamps.TryGetValue(name, out var timestamp)
            ? Math.Round(Stopwatch.GetElapsedTime(_managedEntryAt, timestamp).TotalMilliseconds, 3)
            : null;
        double? ScanDuration(string name) => _timestamps.TryGetValue("scanInvoked", out var start) &&
            _timestamps.TryGetValue(name, out var end)
            ? Math.Round(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, 3)
            : null;

        double? processToManaged = _processStartedUtc is null ? null : Math.Round((_managedEntryUtc - _processStartedUtc.Value).TotalMilliseconds, 3);
        return new NativeBenchmarkTimings(
            processToManaged,
            Math.Round(Stopwatch.GetElapsedTime(_managedEntryAt, _configValidatedAt).TotalMilliseconds, 3),
            Duration("windowOpened"),
            Duration("scanInvoked"),
            ScanDuration("firstModelRow"),
            ScanDuration("finalModelState"),
            ScanDuration("scanDrained"),
            ScanDuration("compositionBatchRendered"),
            ScanDuration("dwmFlushReturned"),
            ScanDuration("nativeClientCaptureCompleted"),
            Duration("nativeClientCaptureCompleted"));
    }

    private NativeBenchmarkFinalState? SnapshotFinalState() => _library is null ? null : new NativeBenchmarkFinalState(
        _library.IsScanning,
        _library.Replays.Count,
        _library.TotalMatches,
        _library.ScanStatusText,
        _library.ScanOutcomeText,
        _library.ErrorMessage,
        _library.AvailableReplayCount,
        _library.SelectedReplayCount,
        _library.ProcessedReplayCount,
        _library.LoadedReplayCount,
        _library.FailedReplayCount);

    private void Mark(string name) => _timestamps.TryAdd(name, Stopwatch.GetTimestamp());

    private void WriteResult(NativeBenchmarkResult result)
    {
        var outputPath = Path.GetFullPath(_config.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = outputPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(result, NativeBenchmarkJson.Options));
        File.Move(temporaryPath, outputPath, overwrite: true);
    }
}

internal sealed record NativeBenchmarkConfig(
    string ReplayDirectory,
    string ManifestPath,
    string SettingsPath,
    string CachePath,
    string OutputPath,
    string ViewportPngPath,
    string NativeClientCapturePath,
    int ScanLimit,
    int MaxConcurrency,
    string ScanProfile,
    bool RequireColdCache,
    bool NativeVisible,
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight,
    int CompletionTimeoutSeconds)
{
    internal NativeReplayManifest Manifest { get; init; } = null!;

    public static NativeBenchmarkConfig Load(string configPath)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullConfigPath))
            throw new FileNotFoundException("Native benchmark config was not found.", fullConfigPath);
        var config = JsonSerializer.Deserialize<NativeBenchmarkConfig>(File.ReadAllText(fullConfigPath), NativeBenchmarkJson.Options)
            ?? throw new InvalidDataException("Native benchmark config is empty.");
        var normalized = config with
        {
            ReplayDirectory = Path.GetFullPath(config.ReplayDirectory),
            ManifestPath = Path.GetFullPath(config.ManifestPath),
            SettingsPath = Path.GetFullPath(config.SettingsPath),
            CachePath = Path.GetFullPath(config.CachePath),
            OutputPath = Path.GetFullPath(config.OutputPath),
            ViewportPngPath = Path.GetFullPath(config.ViewportPngPath),
            NativeClientCapturePath = Path.GetFullPath(config.NativeClientCapturePath)
        };
        normalized.Validate();
        return normalized with { Manifest = NativeManifestValidator.LoadAndValidate(normalized.ManifestPath, normalized.ReplayDirectory) };
    }

    public void Validate()
    {
        if (!Directory.Exists(ReplayDirectory))
            throw new DirectoryNotFoundException("The benchmark replay directory does not exist.");
        if (ScanLimit <= 0)
            throw new InvalidDataException("scanLimit must be positive.");
        if (MaxConcurrency is not (1 or 2 or 4))
            throw new InvalidDataException("maxConcurrency must be 1, 2, or 4.");
        if (!string.Equals(ScanProfile, "summary", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("scanProfile must be summary for the current native benchmark route.");
        if (!RequireColdCache)
            throw new InvalidDataException("requireColdCache must be true; warm runs require a separate route.");
        if (File.Exists(CachePath) || Directory.Exists(CachePath))
            throw new InvalidDataException("cachePath must not exist for a cold-cache benchmark; this route never deletes it.");
        if (File.Exists(SettingsPath) || Directory.Exists(SettingsPath))
            throw new InvalidDataException("settingsPath must not exist; provide an isolated, fresh settings file.");
        if (!NativeVisible)
            throw new InvalidDataException("nativeVisible must be true; this route measures a real native window.");
        if (WindowWidth < 320 || WindowHeight < 240)
            throw new InvalidDataException("windowWidth and windowHeight must describe a usable visible native window.");
        if (CompletionTimeoutSeconds is < 1 or > 600)
            throw new InvalidDataException("completionTimeoutSeconds must be between 1 and 600.");
        if (new[] { ManifestPath, SettingsPath, CachePath, OutputPath, ViewportPngPath, NativeClientCapturePath }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("manifestPath and all output paths are required.");
        if (new[] { SettingsPath, CachePath, OutputPath, ViewportPngPath, NativeClientCapturePath }
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
            throw new InvalidDataException("settings, cache, result, software PNG, and native capture paths must be distinct.");
    }
}

// This is the same schema, identity ordering, length/mtime/SHA-256, and no-extra-file validation
// used by tools/ReplayBenchmark. It is local so the production executable has no runtime tool dependency.
internal sealed record NativeReplayManifest(int SchemaVersion, DateTime CreatedUtc, string RootHint, IReadOnlyList<NativeReplayManifestEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

internal sealed record NativeReplayManifestEntry(string RelativePath, long Length, DateTime LastWriteTimeUtc, string Sha256,
    int AllRank, int? Newest50Rank);

internal static class NativeManifestValidator
{
    public static NativeReplayManifest LoadAndValidate(string manifestPath, string replayRoot)
    {
        var manifest = JsonSerializer.Deserialize<NativeReplayManifest>(File.ReadAllText(manifestPath), NativeBenchmarkJson.Options)
            ?? throw new InvalidDataException("Replay manifest is empty.");
        if (manifest.SchemaVersion != NativeReplayManifest.CurrentSchemaVersion || manifest.Entries is null || manifest.Entries.Count == 0)
            throw new InvalidDataException("Replay manifest schema or entries are invalid.");

        var root = Path.GetFullPath(replayRoot);
        var ordered = manifest.Entries.OrderBy(entry => entry.AllRank).ToArray();
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var entry = ordered[index];
            if (entry.AllRank != index + 1 || string.IsNullOrWhiteSpace(entry.RelativePath) || entry.Length < 0 ||
                string.IsNullOrWhiteSpace(entry.Sha256) || entry.Sha256.Length != 64)
            {
                problems.Add($"manifest entry {index + 1}: invalid identity fields");
                continue;
            }
            var path = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !expectedPaths.Add(entry.RelativePath))
            {
                problems.Add($"{entry.RelativePath}: duplicate or escapes replay root");
                continue;
            }
            if (!File.Exists(path))
            {
                problems.Add($"{entry.RelativePath}: missing");
                continue;
            }
            var file = new FileInfo(path);
            if (file.Length != entry.Length)
                problems.Add($"{entry.RelativePath}: length changed");
            if (file.LastWriteTimeUtc != entry.LastWriteTimeUtc)
                problems.Add($"{entry.RelativePath}: last-write time changed");
            using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), entry.Sha256, StringComparison.Ordinal))
                problems.Add($"{entry.RelativePath}: SHA-256 changed");
        }

        var actual = Directory.EnumerateFiles(root, "*.replay", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FullName, StringComparer.Ordinal)
            .ToArray();
        if (actual.Length != ordered.Length)
            problems.Add($"replay file count expected {ordered.Length}, got {actual.Length}");
        for (var index = 0; index < Math.Min(actual.Length, ordered.Length); index++)
        {
            var relative = Path.GetRelativePath(root, actual[index].FullName);
            if (!string.Equals(relative, ordered[index].RelativePath, StringComparison.OrdinalIgnoreCase))
                problems.Add($"rank {index + 1}: manifest selection differs from product replay ordering");
        }
        if (problems.Count > 0)
            throw new InvalidDataException("Frozen replay input changed: " + string.Join("; ", problems));
        return manifest;
    }

    public static string Fingerprint(NativeReplayManifest manifest) =>
        NativeBenchmarkJson.Fingerprint(manifest.Entries.OrderBy(entry => entry.AllRank).ToArray());
}

internal sealed record NativeBenchmarkResult(
    int SchemaVersion,
    bool Succeeded,
    string? Error,
    string ScanProfile,
    int ScanLimit,
    int ConfiguredMaxConcurrency,
    int? ObservedMaxConcurrency,
    bool ColdCacheRequired,
    bool NativeVisible,
    DateTimeOffset? ProcessStartedUtc,
    DateTimeOffset ManagedEntryUtc,
    bool DwmFlushSucceeded,
    string? DwmFlushError,
    NativeSoftwareViewportCapture? SoftwareViewportDiagnostic,
    NativeClientViewportCapture? NativeClientViewportCapture,
    NativeBenchmarkVerification? Verification,
    NativeBenchmarkTimings TimingsMs,
    NativeBenchmarkFinalState? FinalState,
    NativeCacheState? CacheBefore,
    NativeCacheState CacheAfter,
    NativeProcessMetrics ProcessMetrics)
{
    public const int CurrentSchemaVersion = 2;
}

internal sealed record NativeSoftwareViewportCapture(bool Succeeded, string? Error, string Method, int PixelWidth, int PixelHeight, long Bytes);
internal sealed record NativeClientViewportCapture(bool Succeeded, string? Error, string Method, int PixelWidth, int PixelHeight, long Bytes);
internal sealed record NativeBenchmarkVerification(bool ManifestValidated, string ManifestFingerprint, int ManifestReplayCount,
    int ExpectedSelectedReplayCount, bool ScanFinalStateValid, bool DataGridItemsSourceBound, int RealizedDataGridRows,
    bool ScanStatusRendered, bool ScanOutcomeRendered, bool TotalMatchesRendered, string SummaryProjectionFingerprint,
    IReadOnlyList<string> Errors);
internal sealed record NativeBenchmarkTimings(double? ProcessStartToManagedEntry, double ConfigValidation,
    double? ManagedEntryToWindowOpened, double? ManagedEntryToScanInvoked,
    double? ScanInvokedToFirstModelRow, double? ScanInvokedToFinalModelState, double? ScanInvokedToScanDrained,
    double? ScanInvokedToCompositionBatchRendered, double? ScanInvokedToDwmFlush, double? ScanInvokedToNativeClientCapture,
    double? ManagedEntryToNativeClientCapture);
internal sealed record NativeBenchmarkFinalState(bool IsScanning, int ReplayRows, int TotalMatches, string ScanStatus,
    string ScanOutcome, string? ErrorMessage, int AvailableReplayCount, int SelectedReplayCount, int ProcessedReplayCount,
    int LoadedReplayCount, int FailedReplayCount);
internal sealed record NativeCacheState(bool Exists, long Bytes, DateTimeOffset? LastWriteUtc)
{
    public static NativeCacheState Read(string path)
    {
        if (!File.Exists(path))
            return new NativeCacheState(false, 0, null);
        var info = new FileInfo(path);
        return new NativeCacheState(true, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }
}

internal sealed record NativeProcessSample(long AllocatedBytes, double CpuMilliseconds, long WorkingSetBytes, long PeakWorkingSetBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections)
{
    public static NativeProcessSample Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new NativeProcessSample(GC.GetTotalAllocatedBytes(precise: false), process.TotalProcessorTime.TotalMilliseconds,
            process.WorkingSet64, process.PeakWorkingSet64, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
}

internal sealed record NativeProcessMetrics(long AllocatedBytesDelta, double CpuMillisecondsDelta, long WorkingSetBytesAtEnd,
    long PeakWorkingSetBytesAtEnd, int Gen0CollectionsDelta, int Gen1CollectionsDelta, int Gen2CollectionsDelta)
{
    public static NativeProcessMetrics Create(NativeProcessSample? before, NativeProcessSample after) => before is null
        ? new NativeProcessMetrics(0, 0, after.WorkingSetBytes, after.PeakWorkingSetBytes, 0, 0, 0)
        : new NativeProcessMetrics(Math.Max(0, after.AllocatedBytes - before.AllocatedBytes),
            Math.Max(0, after.CpuMilliseconds - before.CpuMilliseconds), after.WorkingSetBytes, after.PeakWorkingSetBytes,
            Math.Max(0, after.Gen0Collections - before.Gen0Collections), Math.Max(0, after.Gen1Collections - before.Gen1Collections),
            Math.Max(0, after.Gen2Collections - before.Gen2Collections));
}

internal sealed record NativeSummaryProjection(IReadOnlyList<NativeReplayProjection> Replays, int TotalMatches, int TotalWins,
    double WinRate, double? AvgKills, double AvgBotPercent, int IncompleteOpponentMatches, IReadOnlyList<NativeOpponentProjection> FrequentOpponents);
internal sealed record NativeReplayProjection(string FilePath, DateTime FileDateUtc, string Playlist, string GameMode, string Placement,
    int? Kills, int? PlayerKills, int? BotKills, int PlayerCount, int BotCount, double DurationMinutes, bool IsWin, double BotPercent,
    string AnalysisStatus, bool OpponentAnalysisComplete);
internal sealed record NativeOpponentProjection(string StableId, string Name, int Appearances);

internal static class NativeBenchmarkJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Fingerprint<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(Options) { WriteIndented = false });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

internal static class NativeCompositor
{
    public static bool TryFlush(out string? error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "DwmFlush is available only on Windows.";
            return false;
        }

        try
        {
            var hresult = DwmFlush();
            if (hresult >= 0)
            {
                error = null;
                return true;
            }

            error = $"DwmFlush failed with HRESULT 0x{hresult:X8}.";
            return false;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            error = exception.Message;
            return false;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();
}

internal static class NativeClientCapture
{
    private const uint DibRgbColors = 0;
    private const uint PwClientOnly = 1;

    public static NativeClientViewportCapture Capture(Window window, string outputPath)
    {
        if (!OperatingSystem.IsWindows())
            return new NativeClientViewportCapture(false, "PrintWindow is available only on Windows.", "Win32 PrintWindow(PW_CLIENTONLY)", 0, 0, 0);
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var rect))
            return new NativeClientViewportCapture(false, "Native client handle or size was unavailable.", "Win32 PrintWindow(PW_CLIENTONLY)", 0, 0, 0);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return new NativeClientViewportCapture(false, "Native client size was empty.", "Win32 PrintWindow(PW_CLIENTONLY)", width, height, 0);

        var sourceDc = GetDC(hwnd);
        var memoryDc = sourceDc == IntPtr.Zero ? IntPtr.Zero : CreateCompatibleDC(sourceDc);
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            if (sourceDc == IntPtr.Zero || memoryDc == IntPtr.Zero)
                throw new InvalidOperationException("Could not create a native client device context.");
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 } };
            bitmap = CreateDIBSection(sourceDc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate a native client bitmap.");
            previous = SelectObject(memoryDc, bitmap);
            if (!PrintWindow(hwnd, memoryDc, PwClientOnly))
                throw new InvalidOperationException($"PrintWindow failed with Win32 error {Marshal.GetLastWin32Error()}.");

            var byteCount = checked(width * height * 4);
            var pixels = new byte[byteCount];
            Marshal.Copy(bits, pixels, 0, byteCount);
            var fullOutput = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            WriteBmp(fullOutput, width, height, pixels);
            return new NativeClientViewportCapture(true, null, "Win32 PrintWindow(PW_CLIENTONLY) 32-bit client DIB", width, height, new FileInfo(fullOutput).Length);
        }
        catch (Exception exception)
        {
            return new NativeClientViewportCapture(false, exception.Message, "Win32 PrintWindow(PW_CLIENTONLY)", width, height, 0);
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
                SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero)
                DeleteDC(memoryDc);
            if (sourceDc != IntPtr.Zero)
                ReleaseDC(hwnd, sourceDc);
        }
    }

    private static void WriteBmp(string path, int width, int height, byte[] pixels)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write((ushort)0x4D42); writer.Write(54 + pixels.Length); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(pixels);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount; public uint Compression;
        public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter; public uint ClrUsed; public uint ClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
}
