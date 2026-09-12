using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
        if (Interlocked.CompareExchange(ref _current, NativeBenchmarkRun.Load(configPath), null) is not null)
            throw new InvalidOperationException("A native benchmark configuration is already active.");
    }
}

internal sealed class NativeBenchmarkRun : ILibraryScanObserver
{
    private readonly NativeBenchmarkConfig _config;
    private readonly long _processStartedAt;
    private readonly ConcurrentDictionary<string, long> _timestamps = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _windowOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _windowLaidOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completionStarted;
    private int _scanDrained;
    private MainWindow? _window;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private LibraryViewModel? _library;

    private NativeBenchmarkRun(NativeBenchmarkConfig config, long processStartedAt)
    {
        _config = config;
        _processStartedAt = processStartedAt;
    }

    public static NativeBenchmarkRun Load(string configPath) =>
        new(NativeBenchmarkConfig.Load(configPath), Stopwatch.GetTimestamp());

    public NativeBenchmarkConfig Config => _config;

    public void Attach(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, LibraryViewModel library)
    {
        _window = window;
        _desktop = desktop;
        _library = library;
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
        TryStartCompletion();
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
        try
        {
            var window = _window ?? throw new InvalidOperationException("Benchmark window was not attached.");
            await Task.WhenAll(_windowOpened.Task, _windowLaidOut.Task)
                .WaitAsync(TimeSpan.FromSeconds(_config.CompletionTimeoutSeconds)).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            Mark("renderPriorityDrained");

            var compositorFlush = NativeCompositor.TryFlush(out var flushError);
            Mark("dwmFlushReturned");
            var screenshot = await Dispatcher.UIThread.InvokeAsync(() => CaptureViewport(window), DispatcherPriority.Render);
            Mark("viewportPngCaptured");
            WriteResult(new NativeBenchmarkResult(
                NativeBenchmarkResult.CurrentSchemaVersion,
                compositorFlush,
                compositorFlush ? null : flushError,
                _config.ScanProfile,
                _config.ScanLimit,
                _config.MaxConcurrency,
                _config.RequireColdCache,
                compositorFlush,
                flushError,
                screenshot,
                SnapshotTimings(),
                SnapshotFinalState()));
        }
        catch (Exception exception)
        {
            WriteResult(new NativeBenchmarkResult(
                NativeBenchmarkResult.CurrentSchemaVersion,
                false,
                exception.Message,
                _config.ScanProfile,
                _config.ScanLimit,
                _config.MaxConcurrency,
                _config.RequireColdCache,
                false,
                null,
                null,
                SnapshotTimings(),
                SnapshotFinalState()));
        }
        finally
        {
            if (_desktop is not null)
                Dispatcher.UIThread.Post(() => _desktop.Shutdown(), DispatcherPriority.Background);
        }
    }

    private NativeViewportCapture CaptureViewport(Window window)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width * window.RenderScaling));
        var height = Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height * window.RenderScaling));
        var outputPath = Path.GetFullPath(_config.ViewportPngPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(outputPath);
        return new NativeViewportCapture("Avalonia RenderTargetBitmap after native Window.Opened, layout, render priority, and DwmFlush", width, height,
            new FileInfo(outputPath).Length);
    }

    private NativeBenchmarkTimings SnapshotTimings()
    {
        double? Duration(string name) => _timestamps.TryGetValue(name, out var timestamp)
            ? Math.Round(Stopwatch.GetElapsedTime(_processStartedAt, timestamp).TotalMilliseconds, 3)
            : null;
        double? ScanDuration(string name) => _timestamps.TryGetValue("scanInvoked", out var start) &&
            _timestamps.TryGetValue(name, out var end)
            ? Math.Round(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, 3)
            : null;

        return new NativeBenchmarkTimings(
            Duration("windowOpened"),
            Duration("scanInvoked"),
            ScanDuration("firstModelRow"),
            ScanDuration("finalModelState"),
            ScanDuration("scanDrained"),
            ScanDuration("dwmFlushReturned"),
            ScanDuration("viewportPngCaptured"),
            Duration("viewportPngCaptured"));
    }

    private NativeBenchmarkFinalState? SnapshotFinalState() => _library is null ? null : new NativeBenchmarkFinalState(
        _library.IsScanning,
        _library.Replays.Count,
        _library.TotalMatches,
        _library.ScanStatusText,
        _library.ScanOutcomeText,
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
    string SettingsPath,
    string CachePath,
    string OutputPath,
    string ViewportPngPath,
    int ScanLimit,
    int MaxConcurrency,
    string ScanProfile,
    bool RequireColdCache,
    int CompletionTimeoutSeconds)
{
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
            SettingsPath = Path.GetFullPath(config.SettingsPath),
            CachePath = Path.GetFullPath(config.CachePath),
            OutputPath = Path.GetFullPath(config.OutputPath),
            ViewportPngPath = Path.GetFullPath(config.ViewportPngPath)
        };
        normalized.Validate();
        return normalized;
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
        if (File.Exists(CachePath))
            throw new InvalidDataException("cachePath must not exist for a cold-cache benchmark; this route never deletes it.");
        if (File.Exists(SettingsPath))
            throw new InvalidDataException("settingsPath must not exist; provide an isolated, fresh settings file.");
        if (CompletionTimeoutSeconds is < 1 or > 600)
            throw new InvalidDataException("completionTimeoutSeconds must be between 1 and 600.");
        if (new[] { SettingsPath, CachePath, OutputPath, ViewportPngPath }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("settingsPath, cachePath, outputPath, and viewportPngPath are required.");
        if (new[] { SettingsPath, CachePath, OutputPath, ViewportPngPath }
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            throw new InvalidDataException("settingsPath, cachePath, outputPath, and viewportPngPath must be distinct.");
    }
}

internal sealed record NativeBenchmarkResult(
    int SchemaVersion,
    bool Succeeded,
    string? Error,
    string ScanProfile,
    int ScanLimit,
    int MaxConcurrency,
    bool ColdCacheRequired,
    bool DwmFlushSucceeded,
    string? DwmFlushError,
    NativeViewportCapture? ViewportCapture,
    NativeBenchmarkTimings TimingsMs,
    NativeBenchmarkFinalState? FinalState)
{
    public const int CurrentSchemaVersion = 1;
}

internal sealed record NativeViewportCapture(string Method, int PixelWidth, int PixelHeight, long PngBytes);
internal sealed record NativeBenchmarkTimings(double? ProcessLaunchToWindowOpened, double? ProcessLaunchToScanInvoked,
    double? ScanInvokedToFirstModelRow, double? ScanInvokedToFinalModelState, double? ScanInvokedToScanDrained,
    double? ScanInvokedToDwmFlush, double? ScanInvokedToViewportPng, double? ProcessLaunchToViewportPng);
internal sealed record NativeBenchmarkFinalState(bool IsScanning, int ReplayRows, int TotalMatches, string ScanStatus,
    string ScanOutcome, int AvailableReplayCount, int SelectedReplayCount, int ProcessedReplayCount, int LoadedReplayCount, int FailedReplayCount);

internal static class NativeBenchmarkJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
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
