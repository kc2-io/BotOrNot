using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

var startedAt = Stopwatch.GetTimestamp();
BenchmarkArguments? parsedArguments = null;

try
{
    parsedArguments = BenchmarkArguments.Parse(args);
    ConfigureHeadlessSkia();
    var result = await RunAsync(parsedArguments, startedAt);
    WriteResult(parsedArguments.OutputPath, result);
    Console.WriteLine(JsonSerializer.Serialize(result, BenchmarkJson.Options));
}
catch (Exception exception)
{
    var failure = BenchmarkFailure.From(exception);
    if (parsedArguments?.OutputPath is { } outputPath)
        WriteResult(outputPath, failure);
    Console.Error.WriteLine(JsonSerializer.Serialize(failure, BenchmarkJson.Options));
    Environment.ExitCode = 1;
}

static void ConfigureHeadlessSkia()
{
    AppBuilder.Configure<BotOrNot.Avalonia.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .SetupWithoutStarting();
    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
}

static async Task<BenchmarkResult> RunAsync(BenchmarkArguments arguments, long processStartedAt)
{
    var manifest = arguments.ManifestPath is null ? null : CorpusManifest.Load(arguments.ManifestPath);
    manifest?.ValidateFixtureDirectory(arguments.FixtureDirectory!);
    var settings = new MemorySettingsService(new AppSettings
    {
        ReplayDirectory = arguments.Mode == ScanMode.Auto ? arguments.FixtureDirectory : null,
        ReplayScanLimit = arguments.Limit,
        Theme = ThemePreference.Dark
    });
    var observer = new TimingObserver();
    var cache = arguments.SelfTest
        ? new RecordingReplayCacheService(new SyntheticReplayCacheService())
        : new RecordingReplayCacheService(new ReplayCacheService(cachePath: arguments.CachePath));

    using var viewModel = new LibraryViewModel(
        _ => { }, cache, settings,
        () => new ReplayScanOptions { MaxConcurrency = arguments.Concurrency }, observer);

    if (arguments.Mode == ScanMode.Manual)
    {
        viewModel.DirectoryPath = arguments.FixtureDirectory;
        viewModel.ScanCommand.Execute().Subscribe();
    }

    var view = new LibraryView { DataContext = viewModel };
    var window = new Window
    {
        Width = arguments.Width,
        Height = arguments.Height,
        Content = view
    };

    try
    {
        // This is an Avalonia headless top level; it never creates a native desktop window.
        window.Show();
        window.UpdateLayout();
        await WaitForFinalModelAsync(viewModel, observer, arguments.Timeout);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var render = CaptureRenderedFrame(window, arguments.Width, arguments.Height, arguments.RenderPngPath);
        var finalFrameAt = Stopwatch.GetTimestamp();
        await WaitForScanDrainedAsync(observer, arguments.Timeout);
        var drainedAt = Stopwatch.GetTimestamp();

        if (!render.HasVisibleContent)
            throw new InvalidOperationException("The Skia-rendered LibraryView bitmap contains no visible content.");
        if (cache.ObservedScanOptions is null)
            throw new InvalidOperationException("The library scan did not invoke IReplayCacheService.ScanAsync.");
        if (cache.ObservedScanOptions.MaxConcurrency != arguments.Concurrency)
            throw new InvalidOperationException("The requested scan concurrency was not passed to IReplayCacheService.ScanAsync.");
        if (!observer.TryGet(LibraryScanMilestone.FinalModelState, out var finalModelAt))
            throw new InvalidOperationException("The library scan did not report a final model state.");

        observer.TryGet(LibraryScanMilestone.Invoked, out var invokedAt);
        observer.TryGet(LibraryScanMilestone.FirstModelRow, out var firstRowAt);
        return new BenchmarkResult
        {
            SchemaVersion = 1,
            Renderer = "Skia offscreen headless",
            NativeDesktopRenderer = false,
            NativeEvidenceNote = "This measures Avalonia's offscreen Skia headless renderer. It is not native desktop viewport-latency evidence.",
            Host = "LibraryView hosted in a headless Window after App XAML setup",
            StartupEvidenceNote = "benchmarkProcessLaunch begins before manifest validation and App XAML setup. The harness does not run the production Program or MainWindow startup path.",
            ScanMode = arguments.Mode.ToString().ToLowerInvariant(),
            SelfTest = arguments.SelfTest,
            ManifestEntryCount = manifest?.Entries.Count,
            Configuration = new BenchmarkConfiguration(arguments.Limit, arguments.Concurrency, arguments.Width, arguments.Height),
            TimingMs = new BenchmarkTiming(
                Since(processStartedAt, invokedAt),
                firstRowAt == 0 ? null : Since(invokedAt, firstRowAt),
                Since(invokedAt, finalModelAt),
                Since(invokedAt, finalFrameAt),
                Since(invokedAt, drainedAt),
                Since(processStartedAt, finalFrameAt)),
            FinalState = new BenchmarkFinalState(
                viewModel.IsScanning,
                viewModel.Replays.Count,
                viewModel.TotalMatches,
                viewModel.TotalWins,
                viewModel.ScanStatusText,
                viewModel.ScanOutcomeText,
                viewModel.AvailableReplayCount,
                viewModel.SelectedReplayCount,
                viewModel.ProcessedReplayCount,
                viewModel.LoadedReplayCount,
                viewModel.FailedReplayCount,
                cache.ObservedScanOptions.Limit,
                cache.ObservedScanOptions.MaxConcurrency),
            Render = render
        };
    }
    finally
    {
        window.Close();
    }
}

static Task WaitForScanDrainedAsync(TimingObserver observer, TimeSpan timeout)
{
    var wait = Stopwatch.StartNew();
    while (!observer.Has(LibraryScanMilestone.ScanDrained))
    {
        Dispatcher.UIThread.RunJobs();
        if (wait.Elapsed > timeout)
            throw new TimeoutException($"Library scan did not drain within {timeout.TotalSeconds:F0} seconds.");
        Thread.Sleep(10);
    }

    return Task.CompletedTask;
}

static Task WaitForFinalModelAsync(LibraryViewModel viewModel, TimingObserver observer, TimeSpan timeout)
{
    var wait = Stopwatch.StartNew();
    while (!observer.Has(LibraryScanMilestone.FinalModelState))
    {
        Dispatcher.UIThread.RunJobs();
        if (wait.Elapsed > timeout)
            throw new TimeoutException($"Library scan did not reach its final model state within {timeout.TotalSeconds:F0} seconds. IsScanning={viewModel.IsScanning}.");
        // The harness owns the headless UI thread. Sleeping here keeps it on that thread so
        // the next RunJobs call can dispatch continuations posted by the streaming scan.
        Thread.Sleep(10);
    }

    return Task.CompletedTask;
}

static BenchmarkRender CaptureRenderedFrame(Window window, int width, int height, string? renderPngPath)
{
    // CaptureRenderedFrame forces a headless renderer tick. Rendering into a RenderTargetBitmap
    // also makes the bitmap check deterministic across Avalonia headless package revisions.
    using var capturedFrame = window.CaptureRenderedFrame();
    if (capturedFrame is null)
        throw new InvalidOperationException("Avalonia did not produce a headless rendered frame.");

    using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
    bitmap.Render(window);
    using var encoded = new MemoryStream();
    bitmap.Save(encoded);

    if (!string.IsNullOrWhiteSpace(renderPngPath))
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(renderPngPath));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        File.WriteAllBytes(renderPngPath, encoded.ToArray());
    }

    return new BenchmarkRender(
        CapturedFrame: true,
        PngByteLength: checked((int)encoded.Length),
        HasVisibleContent: encoded.Length > 1024);
}

static double Since(long start, long end) => Math.Round(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, 3);

static void WriteResult<T>(string? outputPath, T value)
{
    if (string.IsNullOrWhiteSpace(outputPath))
        return;

    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);
    File.WriteAllText(fullPath, JsonSerializer.Serialize(value, BenchmarkJson.Options));
}

internal enum ScanMode { Auto, Manual }

internal sealed record BenchmarkArguments(
    string? FixtureDirectory,
    string? CachePath,
    string? ManifestPath,
    int Concurrency,
    int Limit,
    ScanMode Mode,
    string? OutputPath,
    string? RenderPngPath,
    int Width,
    int Height,
    TimeSpan Timeout,
    bool SelfTest)
{
    public static BenchmarkArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (key is "--help" or "-h")
                throw new ArgumentException(Usage);
            if (!key.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            if (key == "--self-test")
            {
                values[key] = "true";
                continue;
            }
            if (++index >= args.Length)
                throw new ArgumentException(Usage);
            values[key] = args[index];
        }

        var selfTest = values.ContainsKey("--self-test");
        if (selfTest)
            return new BenchmarkArguments("synthetic", null, null, 2, 2, ScanMode.Manual,
                Get(values, "--output", required: false), Get(values, "--render-png", required: false),
                ParsePositive(values, "--width", 1000), ParsePositive(values, "--height", 700),
                TimeSpan.FromSeconds(ParsePositive(values, "--timeout-seconds", 20)), true);

        var mode = Get(values, "--mode", required: false) switch
        {
            null or "auto" => ScanMode.Auto,
            "manual" => ScanMode.Manual,
            _ => throw new ArgumentException("--mode must be auto or manual.\n" + Usage)
        };
        var concurrency = ParsePositive(values, "--concurrency", ReplayScanOptions.DefaultMaxConcurrency);
        if (concurrency is not (1 or 2 or 4))
            throw new ArgumentException("--concurrency must be 1, 2, or 4.\n" + Usage);

        var fixture = Get(values, "--fixture-dir", required: true)!;
        if (!Directory.Exists(fixture))
            throw new DirectoryNotFoundException($"Fixture directory does not exist: {fixture}");
        var cachePath = Get(values, "--cache-path", required: false);
        var cacheDirectory = Get(values, "--cache-dir", required: false);
        if ((cachePath is null) == (cacheDirectory is null))
            throw new ArgumentException("Specify exactly one of --cache-path or --cache-dir.\n" + Usage);
        var cache = cachePath ?? Path.Combine(cacheDirectory!, "replay-cache.json");
        var manifest = Get(values, "--manifest", required: true)!;
        if (!File.Exists(manifest))
            throw new FileNotFoundException("Manifest does not exist.", manifest);
        return new BenchmarkArguments(
            Path.GetFullPath(fixture), Path.GetFullPath(cache), Path.GetFullPath(manifest), concurrency,
            ParsePositive(values, "--limit", ReplayScanOptions.DefaultLimit), mode,
            Get(values, "--output", required: true), Get(values, "--render-png", required: false),
            ParsePositive(values, "--width", 1000), ParsePositive(values, "--height", 700),
            TimeSpan.FromSeconds(ParsePositive(values, "--timeout-seconds", 120)), false);
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, string key, bool required)
    {
        if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        if (required)
            throw new ArgumentException($"{key} is required.\n" + Usage);
        return null;
    }

    private static int ParsePositive(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var value))
            return fallback;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{key} must be a positive integer.\n" + Usage);
        return parsed;
    }

    private const string Usage = "Usage: BotOrNot.LibraryUiBenchmark --fixture-dir <replay directory> --manifest <inventory json> (--cache-path <cache json> | --cache-dir <cache directory>) --concurrency <1|2|4> --output <result json> [--limit <positive>] [--mode auto|manual] [--render-png <png>]\n       BotOrNot.LibraryUiBenchmark --self-test [--output <result json>] [--render-png <png>]";
}

internal sealed class CorpusManifest(IReadOnlyList<CorpusManifestEntry> entries)
{
    public IReadOnlyList<CorpusManifestEntry> Entries { get; } = entries;

    public static CorpusManifest Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entries = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : FindEntryArray(document.RootElement);
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            throw new InvalidDataException("The replay manifest must contain a non-empty files or entries array.");

        var result = new List<CorpusManifestEntry>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each replay manifest entry must be an object.");
            var fileName = GetString(entry, "relativePath", "file", "name", "Name");
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidDataException("Each replay manifest entry must identify a replay file.");
            var expectedLength = GetInt64(entry, "length", "Length", "size");
            result.Add(new CorpusManifestEntry(Path.GetFileName(fileName), expectedLength));
        }

        if (result.Select(entry => entry.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new InvalidDataException("The replay manifest contains duplicate file names.");
        return new CorpusManifest(result);
    }

    public void ValidateFixtureDirectory(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.replay", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
        if (files.Count != Entries.Count)
            throw new InvalidDataException($"Manifest has {Entries.Count} entries but fixture directory has {files.Count} replay files.");

        foreach (var entry in Entries)
        {
            if (!files.TryGetValue(entry.FileName, out var file))
                throw new InvalidDataException("Fixture directory is missing a manifest replay entry.");
            if (entry.Length is { } expectedLength && file.Length != expectedLength)
                throw new InvalidDataException("Fixture replay length does not match the manifest.");
        }
    }

    private static JsonElement FindEntryArray(JsonElement root)
    {
        foreach (var propertyName in new[] { "files", "entries" })
        {
            if (root.TryGetProperty(propertyName, out var value))
                return value;
        }

        return default;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static long? GetInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result))
                return result;
        }

        return null;
    }
}

internal sealed record CorpusManifestEntry(string FileName, long? Length);

internal sealed class TimingObserver : ILibraryScanObserver
{
    private readonly ConcurrentDictionary<LibraryScanMilestone, long> _timestamps = new();

    public void OnMilestone(LibraryScanMilestone milestone) => _timestamps.TryAdd(milestone, Stopwatch.GetTimestamp());
    public bool Has(LibraryScanMilestone milestone) => _timestamps.ContainsKey(milestone);
    public bool TryGet(LibraryScanMilestone milestone, out long timestamp) => _timestamps.TryGetValue(milestone, out timestamp);
}

internal sealed class MemorySettingsService(AppSettings initial) : ISettingsService
{
    private AppSettings _settings = initial;
    public AppSettings Load() => _settings;
    public void Save(AppSettings settings) => _settings = settings;
    public void Update(Action<AppSettings> update) => update(_settings);
}

internal sealed class RecordingReplayCacheService(IReplayCacheService inner) : IReplayCacheService
{
    public ReplayScanOptions? ObservedScanOptions { get; private set; }

    public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        => inner.GetSummariesAsync(directory, progress, cancellationToken);

    public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
        string directory,
        ReplayScanOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObservedScanOptions = options;
        await foreach (var update in inner.ScanAsync(directory, options, cancellationToken).ConfigureAwait(false))
            yield return update;
    }
}

internal sealed class SyntheticReplayCacheService : IReplayCacheService
{
    private static readonly IReadOnlyList<ReplaySummary> Summaries = new[]
    {
        new ReplaySummary { FileName = "sample-one.replay", FilePath = "sample-one.replay", FileDate = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc), Playlist = "Playlist_DefaultSolo", GameMode = "BR Build Solo", Placement = "1", Kills = 5, BotKills = 2, PlayerCount = 100, BotCount = 60, DurationMinutes = 21.2 },
        new ReplaySummary { FileName = "sample-two.replay", FilePath = "sample-two.replay", FileDate = new DateTime(2026, 9, 12, 11, 0, 0, DateTimeKind.Utc), Playlist = "Playlist_DefaultSquad", GameMode = "BR Build Squads", Placement = "12", Kills = 2, BotKills = 1, PlayerCount = 100, BotCount = 55, DurationMinutes = 20.4 }
    };

    public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Summaries);

    public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
        string directory,
        ReplayScanOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scanOptions = options ?? new ReplayScanOptions();
        var scanId = Guid.NewGuid();
        yield return Update(ReplayScanStatus.Started, 0, null);
        foreach (var summary in Summaries.Take(scanOptions.Limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            var processed = Array.IndexOf(Summaries.ToArray(), summary) + 1;
            yield return Update(ReplayScanStatus.Loaded, processed, summary);
        }
        yield return Update(ReplayScanStatus.Completed, Math.Min(scanOptions.Limit, Summaries.Count), null);

        ReplayScanUpdate Update(ReplayScanStatus status, int processed, ReplaySummary? summary) => new()
        {
            ScanId = scanId,
            Status = status,
            ConfiguredLimit = scanOptions.Limit,
            AvailableCount = Summaries.Count,
            SelectedCount = Math.Min(scanOptions.Limit, Summaries.Count),
            ProcessedCount = processed,
            LoadedCount = processed,
            FailedCount = 0,
            Summary = summary,
            File = summary is null ? null : new ReplayFileIdentity(summary.FileName, summary.FilePath, 0, summary.FileDate)
        };
    }
}

internal sealed record BenchmarkResult
{
    public required int SchemaVersion { get; init; }
    public required string Renderer { get; init; }
    public required bool NativeDesktopRenderer { get; init; }
    public required string NativeEvidenceNote { get; init; }
    public required string Host { get; init; }
    public required string StartupEvidenceNote { get; init; }
    public required string ScanMode { get; init; }
    public required bool SelfTest { get; init; }
    public required int? ManifestEntryCount { get; init; }
    public required BenchmarkConfiguration Configuration { get; init; }
    public required BenchmarkTiming TimingMs { get; init; }
    public required BenchmarkFinalState FinalState { get; init; }
    public required BenchmarkRender Render { get; init; }
}

internal sealed record BenchmarkConfiguration(int Limit, int Concurrency, int Width, int Height);
internal sealed record BenchmarkTiming(double BenchmarkProcessLaunchToScanInvocation, double? ScanInvocationToFirstModelRow, double ScanInvocationToFinalModelState, double ScanInvocationToFinalRenderedFrame, double ScanInvocationToScanDrained, double BenchmarkProcessLaunchToFinalRenderedFrame);
internal sealed record BenchmarkFinalState(bool IsScanning, int ReplayRows, int TotalMatches, int TotalWins, string ScanStatus, string ScanOutcome, int AvailableReplayCount, int SelectedReplayCount, int ProcessedReplayCount, int LoadedReplayCount, int FailedReplayCount, int ObservedLimit, int ObservedConcurrency);
internal sealed record BenchmarkRender(bool CapturedFrame, int PngByteLength, bool HasVisibleContent);
internal sealed record BenchmarkFailure(int SchemaVersion, string ErrorType, string Message)
{
    public static BenchmarkFailure From(Exception exception) => new(1, exception.GetType().Name, exception.Message);
}

internal static class BenchmarkJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
