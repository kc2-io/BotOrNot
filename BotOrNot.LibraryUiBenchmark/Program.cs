using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;
using ReplayBenchmark;

var startedAt = Stopwatch.GetTimestamp();
BenchmarkArguments? parsedArguments = null;

try
{
    parsedArguments = BenchmarkArguments.Parse(args);
    ConfigureHeadlessSkia();
    // SetupWithoutStarting installs the Avalonia synchronization context, but it does not
    // start the dispatcher loop. Pump while the asynchronous harness runs so continuations
    // from manifest/cache/resource work and the view-model scan can return to this UI thread.
    var result = RunWithDispatcherPump(() => RunAsync(parsedArguments, startedAt));
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

static T RunWithDispatcherPump<T>(Func<Task<T>> operation)
{
    var task = operation();
    while (!task.IsCompleted)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(1);
    }

    Dispatcher.UIThread.RunJobs();
    return task.GetAwaiter().GetResult();
}

static async Task<BenchmarkResult> RunAsync(BenchmarkArguments arguments, long processStartedAt)
{
    var manifest = arguments.ManifestPath is null ? null : await BenchmarkManifest.LoadAndValidateAsync(
        arguments.ManifestPath, arguments.FixtureDirectory!);
    var process = Process.GetCurrentProcess();
    process.Refresh();
    var initialCpu = process.TotalProcessorTime;
    var initialAllocations = GC.GetTotalAllocatedBytes(true);
    var initialGen0 = GC.CollectionCount(0);
    var initialGen1 = GC.CollectionCount(1);
    var initialGen2 = GC.CollectionCount(2);
    var cacheEvidence = BenchmarkCacheEvidence.Before(arguments);
    var settings = new MemorySettingsService(new AppSettings
    {
        ReplayDirectory = arguments.Mode == ScanMode.Auto ? arguments.FixtureDirectory : null,
        ReplayScanLimit = arguments.Limit,
        Theme = ThemePreference.Dark
    });
    var observer = new TimingObserver();
    var cache = arguments.SelfTest
        ? new RecordingReplayCacheService(new SyntheticReplayCacheService())
        : new RecordingReplayCacheService(new ReplayCacheService(
            arguments.Profile == "normal" ? new FullOnlyReplayService() : null, cachePath: arguments.CachePath));

    using var sampler = new ProcessResourceSampler(process);
    using var viewModel = new LibraryViewModel(
        _ => { }, cache, settings,
        () => new ReplayScanOptions { MaxConcurrency = arguments.Concurrency }, observer);

    if (arguments.Mode == ScanMode.Manual)
        viewModel.DirectoryPath = arguments.FixtureDirectory;

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
        using (window.CaptureRenderedFrame())
        {
            // A manual scan is deliberately triggered only after this initial empty library frame.
        }
        var interactiveFrameAt = Stopwatch.GetTimestamp();
        if (arguments.Mode == ScanMode.Manual)
            viewModel.ScanCommand.Execute().Subscribe();
        await WaitForFinalModelAsync(viewModel, observer, arguments.Timeout);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var visual = VerifyVisibleLibraryState(view, viewModel, expectedCount: arguments.SelfTest ? 2 : Math.Min(manifest!.Entries.Count, arguments.Limit));
        var render = CaptureRenderedFrame(window, arguments.Width, arguments.Height, arguments.RenderPngPath);
        var finalFrameAt = Stopwatch.GetTimestamp();
        await WaitForScanDrainedAsync(observer, arguments.Timeout);
        if (!observer.TryGet(LibraryScanMilestone.ScanDrained, out var drainedAt))
            throw new InvalidOperationException("The library scan did not report its disposal boundary.");
        var expectedCount = arguments.SelfTest ? 2 : Math.Min(manifest!.Entries.Count, arguments.Limit);
        if (viewModel.IsScanning || viewModel.FailedReplayCount != 0 ||
            viewModel.Replays.Count != expectedCount || viewModel.TotalMatches != expectedCount ||
            viewModel.ProcessedReplayCount != expectedCount || viewModel.LoadedReplayCount != expectedCount)
            throw new InvalidOperationException("Final library state does not contain every selected replay without failures.");

        if (!render.HasVisibleContent)
            throw new InvalidOperationException("The Skia-rendered LibraryView bitmap contains no visible content.");
        if (cache.ObservedScanOptions is null)
            throw new InvalidOperationException("The library scan did not invoke IReplayCacheService.ScanAsync.");
        if (cache.ObservedScanOptions.MaxConcurrency != arguments.Concurrency)
            throw new InvalidOperationException("The requested scan concurrency was not passed to IReplayCacheService.ScanAsync.");
        if (cache.ObservedScanOptions.Limit != arguments.Limit)
            throw new InvalidOperationException("The requested scan limit was not passed to IReplayCacheService.ScanAsync.");
        if (!observer.TryGet(LibraryScanMilestone.FinalModelState, out var finalModelAt))
            throw new InvalidOperationException("The library scan did not report a final model state.");

        if (!observer.TryGet(LibraryScanMilestone.Invoked, out var invokedAt))
            throw new InvalidOperationException("The library scan did not report its invocation boundary.");
        observer.TryGet(LibraryScanMilestone.FirstModelRow, out var firstRowAt);
        await sampler.StopAsync();
        var cacheResult = await cacheEvidence.CompleteAsync(cache, expectedCount);
        process.Refresh();
        var result = new BenchmarkResult
        {
            SchemaVersion = 1,
            Renderer = "Skia offscreen headless",
            NativeDesktopRenderer = false,
            NativeEvidenceNote = "This measures Avalonia's offscreen Skia headless renderer. It is not native desktop viewport-latency evidence.",
            Host = "LibraryView hosted in a headless Window after App XAML setup",
            StartupEvidenceNote = "benchmarkManagedEntry begins at the first managed statement, before manifest validation and App XAML setup. Runtime/process creation and production Program/MainWindow startup are not measured. In manual mode the scan is invoked only after the initial empty LibraryView frame.",
            ScanMode = arguments.Mode.ToString().ToLowerInvariant(),
            Profile = arguments.Profile,
            SelfTest = arguments.SelfTest,
            ManifestEntryCount = manifest?.Entries.Count,
            ManifestFingerprint = manifest is null ? null : ReplayManifestService.Fingerprint(manifest),
            Configuration = new BenchmarkConfiguration(arguments.Limit, arguments.Concurrency, arguments.Width, arguments.Height),
            TimingMs = new BenchmarkTiming(
                Since(processStartedAt, invokedAt),
                Since(processStartedAt, interactiveFrameAt),
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
            Render = render,
            Visual = visual,
            Projection = new BenchmarkProjection(
                FingerprintRowsInDisplayedOrder(viewModel.Replays, manifest, arguments.FixtureDirectory),
                Fingerprint(viewModel.FrequentOpponents),
                viewModel.TotalMatches, viewModel.TotalWins, viewModel.WinRate,
                viewModel.AvgKills, viewModel.AvgKillsDisplay, viewModel.AvgBotPercent,
                viewModel.IncompleteOpponentMatchCount, viewModel.OpponentDataIncompleteText),
            Cache = cacheResult,
            Resources = new BenchmarkResources(
                (process.TotalProcessorTime - initialCpu).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(true) - initialAllocations,
                sampler.PeakWorkingSetBytes, sampler.PeakPrivateBytes,
                GC.CollectionCount(0) - initialGen0, GC.CollectionCount(1) - initialGen1, GC.CollectionCount(2) - initialGen2,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount,
                sampler.SampleIntervalMilliseconds)
        };
        VerifyExpectedResult(arguments.ExpectedResultPath, result);
        return result;
    }
    finally
    {
        window.Close();
    }
}

static string Fingerprint<T>(T value) => OracleJson.Fingerprint(value);

static string FingerprintRowsInDisplayedOrder(
    IReadOnlyList<ReplaySummary> rows, ReplayManifest? manifest, string? fixtureDirectory)
{
    if (manifest is null || fixtureDirectory is null)
        return Fingerprint(rows);

    var root = Path.GetFullPath(fixtureDirectory);
    var identities = manifest.Entries.ToDictionary(
        entry => Path.GetFullPath(Path.Combine(root, entry.RelativePath)),
        StringComparer.OrdinalIgnoreCase);
    var canonical = rows.Select(row =>
    {
        var path = Path.GetFullPath(row.FilePath);
        if (!identities.TryGetValue(path, out var identity))
            throw new InvalidDataException($"Library row path '{row.FilePath}' is not in the frozen manifest.");
        return CanonicalReplaySummary.From(identity, row, path);
    }).ToArray();
    // Deliberately do not sort: this is the displayed library order.
    return Fingerprint(canonical);
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
    var pixelSignal = PixelSignal.FromEncodedPng(encoded.ToArray());

    return new BenchmarkRender(
        CapturedFrame: true,
        PngByteLength: checked((int)encoded.Length),
        HasVisibleContent: pixelSignal.HasDiverseVisibleContent,
        pixelSignal.NonZeroByteCount,
        pixelSignal.DistinctByteValues);
}

static BenchmarkVisualProof VerifyVisibleLibraryState(LibraryView view, LibraryViewModel viewModel, int expectedCount)
{
    var grid = view.FindControl<DataGrid>("ReplayGrid")
        ?? throw new InvalidOperationException("LibraryView did not create ReplayGrid.");
    var gridSource = grid.ItemsSource as System.Collections.ICollection;
    if (gridSource?.Count != expectedCount)
        throw new InvalidOperationException("ReplayGrid does not expose the final replay collection.");

    var realizedRows = grid.GetVisualDescendants().OfType<DataGridRow>()
        .Count(row => row.IsVisible && row.Bounds.Height > 0);
    if (realizedRows == 0)
        throw new InvalidOperationException("ReplayGrid has no realized visible row after the final render tick.");

    var text = view.GetVisualDescendants().OfType<TextBlock>()
        .Select(block => block.Text ?? string.Empty).ToHashSet(StringComparer.Ordinal);
    var expectedText = new List<string>
    {
        viewModel.TotalMatches.ToString(CultureInfo.CurrentCulture),
        viewModel.TotalWins.ToString(CultureInfo.CurrentCulture),
        $"{viewModel.WinRate.ToString("F1", CultureInfo.CurrentCulture)}%",
        viewModel.AvgKillsDisplay,
        $"{viewModel.AvgBotPercent.ToString("F1", CultureInfo.CurrentCulture)}%"
    };
    if (viewModel.HasIncompleteOpponentData)
        expectedText.Add(viewModel.OpponentDataIncompleteText);
    var missing = expectedText.Where(value => !text.Contains(value)).ToArray();
    if (missing.Length > 0)
        throw new InvalidOperationException("Final aggregate text is not visible: " + string.Join(", ", missing));

    return new BenchmarkVisualProof(gridSource.Count, realizedRows, expectedText);
}

static void VerifyExpectedResult(string? expectedResultPath, BenchmarkResult actual)
{
    if (string.IsNullOrWhiteSpace(expectedResultPath))
        return;

    var expected = JsonSerializer.Deserialize<BenchmarkResult>(File.ReadAllText(expectedResultPath), BenchmarkJson.Options)
        ?? throw new InvalidDataException("Could not read the expected benchmark result.");
    if (!string.Equals(expected.ManifestFingerprint, actual.ManifestFingerprint, StringComparison.Ordinal) ||
        expected.Configuration.Limit != actual.Configuration.Limit ||
        expected.Projection != actual.Projection ||
        expected.FinalState.ReplayRows != actual.FinalState.ReplayRows ||
        expected.FinalState.TotalMatches != actual.FinalState.TotalMatches ||
        expected.FinalState.TotalWins != actual.FinalState.TotalWins ||
        expected.FinalState.FailedReplayCount != actual.FinalState.FailedReplayCount)
        throw new InvalidOperationException("Benchmark projection differs from --expected-result.");
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
internal enum CacheMode { None, Cold, Warm }

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
    bool SelfTest,
    string Profile = "summary",
    CacheMode CacheMode = CacheMode.None,
    string? ExpectedResultPath = null)
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
                TimeSpan.FromSeconds(ParsePositive(values, "--timeout-seconds", 20)), true,
                Profile: "summary", CacheMode: CacheMode.None);

        var mode = Get(values, "--mode", required: false) switch
        {
            null or "auto" => ScanMode.Auto,
            "manual" => ScanMode.Manual,
            _ => throw new ArgumentException("--mode must be auto or manual.\n" + Usage)
        };
        var concurrency = ParsePositive(values, "--concurrency", ReplayScanOptions.DefaultMaxConcurrency);
        if (concurrency is not (1 or 2 or 4))
            throw new ArgumentException("--concurrency must be 1, 2, or 4.\n" + Usage);
        var profile = Get(values, "--profile", required: false) ?? "summary";
        if (profile is not ("summary" or "normal"))
            throw new ArgumentException("--profile must be summary or normal.");

        var fixture = Get(values, "--fixture-dir", required: true)!;
        if (!Directory.Exists(fixture))
            throw new DirectoryNotFoundException($"Fixture directory does not exist: {fixture}");
        var cachePath = Get(values, "--cache-path", required: false);
        var cacheDirectory = Get(values, "--cache-dir", required: false);
        if ((cachePath is null) == (cacheDirectory is null))
            throw new ArgumentException("Specify exactly one of --cache-path or --cache-dir.\n" + Usage);
        var cache = cachePath ?? Path.Combine(cacheDirectory!, "replay-cache.json");
        var cacheMode = Get(values, "--cache-mode", required: true)?.ToLowerInvariant() switch
        {
            "cold" => CacheMode.Cold,
            "warm" => CacheMode.Warm,
            _ => throw new ArgumentException("--cache-mode must be cold or warm.\n" + Usage)
        };
        var manifest = Get(values, "--manifest", required: true)!;
        if (!File.Exists(manifest))
            throw new FileNotFoundException("Manifest does not exist.", manifest);
        return new BenchmarkArguments(
            Path.GetFullPath(fixture), Path.GetFullPath(cache), Path.GetFullPath(manifest), concurrency,
            ParsePositive(values, "--limit", ReplayScanOptions.DefaultLimit), mode,
            Get(values, "--output", required: true), Get(values, "--render-png", required: false),
            ParsePositive(values, "--width", 1000), ParsePositive(values, "--height", 700),
            TimeSpan.FromSeconds(ParsePositive(values, "--timeout-seconds", 120)), false, profile, cacheMode,
            Get(values, "--expected-result", required: false));
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

    private const string Usage = "Usage: BotOrNot.LibraryUiBenchmark --fixture-dir <replay directory> --manifest <frozen-manifest json> (--cache-path <cache json> | --cache-dir <cache directory>) --cache-mode <cold|warm> --concurrency <1|2|4> --output <result json> [--limit <positive>] [--mode auto|manual] [--expected-result <normal-ui.json>] [--render-png <png>]\n       BotOrNot.LibraryUiBenchmark --self-test [--output <result json>] [--render-png <png>]";
}

internal static class BenchmarkManifest
{
    public static async Task<ReplayManifest> LoadAndValidateAsync(string manifestPath, string fixtureDirectory)
    {
        var manifest = await OracleJson.ReadAsync<ReplayManifest>(manifestPath).ConfigureAwait(false);
        if (manifest.SchemaVersion != ReplayManifest.CurrentSchemaVersion || manifest.Entries.Count == 0)
            throw new InvalidDataException("The frozen replay manifest is missing entries or has an unsupported schema.");
        var problems = await ReplayManifestService.ValidateAsync(manifest, fixtureDirectory).ConfigureAwait(false);
        if (problems.Count > 0)
            throw new InvalidOperationException("Frozen replay input changed:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        return manifest;
    }
}

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

// Omitting IReplaySummaryService deliberately exercises the full Normal oracle through
// the same cache, streaming, view-model and rendering pipeline.
internal sealed class FullOnlyReplayService : IReplayService
{
    public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
        => new ReplayService().LoadReplayAsync(path, cancellationToken);
}

internal sealed class RecordingReplayCacheService(IReplayCacheService inner) : IReplayCacheService
{
    public ReplayScanOptions? ObservedScanOptions { get; private set; }
    public int CachedCount { get; private set; }
    public int LoadedCount { get; private set; }
    public int FailedCount { get; private set; }

    public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(string directory, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        => inner.GetSummariesAsync(directory, progress, cancellationToken);

    public async IAsyncEnumerable<ReplayScanUpdate> ScanAsync(
        string directory,
        ReplayScanOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObservedScanOptions = options;
        await foreach (var update in inner.ScanAsync(directory, options, cancellationToken).ConfigureAwait(false))
        {
            if (update.Status == ReplayScanStatus.Cached) CachedCount++;
            if (update.Status == ReplayScanStatus.Loaded) LoadedCount++;
            if (update.Status == ReplayScanStatus.Failed) FailedCount++;
            yield return update;
        }
    }
}

internal sealed class BenchmarkCacheEvidence
{
    private readonly BenchmarkArguments _arguments;
    private readonly CacheFileState _before;

    private BenchmarkCacheEvidence(BenchmarkArguments arguments, CacheFileState before)
    {
        _arguments = arguments;
        _before = before;
    }

    public static BenchmarkCacheEvidence Before(BenchmarkArguments arguments)
    {
        if (arguments.SelfTest)
            return new BenchmarkCacheEvidence(arguments, CacheFileState.NotMeasured);
        var before = CacheFileState.Read(arguments.CachePath!);
        if (arguments.CacheMode == CacheMode.Cold && before.Exists)
            throw new InvalidOperationException("--cache-mode cold requires an absent cache file; the harness never deletes cache data.");
        if (arguments.CacheMode == CacheMode.Warm && !before.Exists)
            throw new InvalidOperationException("--cache-mode warm requires a pre-populated cache file.");
        return new BenchmarkCacheEvidence(arguments, before);
    }

    public async Task<BenchmarkCache> CompleteAsync(RecordingReplayCacheService cache, int expectedCount)
    {
        if (_arguments.SelfTest)
            return new BenchmarkCache("notMeasured", "notMeasured", _before, _before, cache.CachedCount, cache.LoadedCount, cache.FailedCount);
        var after = await CacheFileState.ReadAsync(_arguments.CachePath!).ConfigureAwait(false);
        if (!after.Exists || after.Length == 0)
            throw new InvalidOperationException("Replay cache was not persisted during the benchmark run.");
        if (_arguments.CacheMode == CacheMode.Cold && (cache.CachedCount != 0 || cache.LoadedCount != expectedCount))
            throw new InvalidOperationException("Cold-cache run emitted cached summaries or did not load every selected replay.");
        if (_arguments.CacheMode == CacheMode.Warm && (cache.LoadedCount != 0 || cache.CachedCount != expectedCount))
            throw new InvalidOperationException("Warm-cache run did not serve every selected replay from cache.");
        if (cache.FailedCount != 0)
            throw new InvalidOperationException("Replay cache emitted failed scan updates.");
        return new BenchmarkCache(
            _arguments.CacheMode.ToString().ToLowerInvariant(),
            "uncontrolled",
            _before, after, cache.CachedCount, cache.LoadedCount, cache.FailedCount);
    }
}

internal sealed record CacheFileState(bool Exists, long Length, string? Sha256)
{
    public static readonly CacheFileState NotMeasured = new(false, 0, null);

    public static CacheFileState Read(string path) => ReadAsync(path).GetAwaiter().GetResult();

    public static async Task<CacheFileState> ReadAsync(string path)
    {
        if (!File.Exists(path))
            return new CacheFileState(false, 0, null);
        var file = new FileInfo(path);
        await using var stream = File.OpenRead(path);
        return new CacheFileState(true, file.Length,
            Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)));
    }
}

internal sealed class ProcessResourceSampler : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _samplingTask;
    private long _peakWorkingSetBytes;
    private long _peakPrivateBytes;

    public int SampleIntervalMilliseconds { get; } = 10;
    public long PeakWorkingSetBytes => Volatile.Read(ref _peakWorkingSetBytes);
    public long PeakPrivateBytes => Volatile.Read(ref _peakPrivateBytes);

    public ProcessResourceSampler(Process process)
    {
        _process = process;
        Capture();
        _samplingTask = Task.Run(SampleAsync);
    }

    public async Task StopAsync()
    {
        _cancellation.Cancel();
        try { await _samplingTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        Capture();
    }

    public void Dispose() => _cancellation.Cancel();

    private async Task SampleAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            await Task.Delay(SampleIntervalMilliseconds, _cancellation.Token).ConfigureAwait(false);
            Capture();
        }
    }

    private void Capture()
    {
        try
        {
            _process.Refresh();
            UpdatePeak(ref _peakWorkingSetBytes, _process.WorkingSet64);
            UpdatePeak(ref _peakPrivateBytes, _process.PrivateMemorySize64);
        }
        catch (InvalidOperationException) { }
    }

    private static void UpdatePeak(ref long target, long candidate)
    {
        var observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref target, candidate, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }
}

internal readonly record struct PixelSignal(int NonZeroByteCount, int DistinctByteValues)
{
    public bool HasDiverseVisibleContent => NonZeroByteCount > 1024 && DistinctByteValues >= 8;

    public static PixelSignal FromEncodedPng(byte[] png)
    {
        using var idat = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = checked((png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3]);
            if (length < 0 || offset + 12L + length > png.Length)
                throw new InvalidDataException("Rendered PNG has an invalid chunk length.");
            if (png[offset + 4] == (byte)'I' && png[offset + 5] == (byte)'D' && png[offset + 6] == (byte)'A' && png[offset + 7] == (byte)'T')
                idat.Write(png, offset + 8, length);
            offset += 12 + length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var pixels = raw.GetBuffer().AsSpan(0, checked((int)raw.Length));
        var distinct = new bool[256];
        var distinctCount = 0;
        var nonZero = 0;
        foreach (var pixel in pixels)
        {
            if (pixel != 0) nonZero++;
            if (!distinct[pixel])
            {
                distinct[pixel] = true;
                distinctCount++;
            }
        }
        return new PixelSignal(nonZero, distinctCount);
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
    public required string Profile { get; init; }
    public required bool SelfTest { get; init; }
    public required int? ManifestEntryCount { get; init; }
    public required string? ManifestFingerprint { get; init; }
    public required BenchmarkConfiguration Configuration { get; init; }
    public required BenchmarkTiming TimingMs { get; init; }
    public required BenchmarkFinalState FinalState { get; init; }
    public required BenchmarkRender Render { get; init; }
    public required BenchmarkVisualProof Visual { get; init; }
    public required BenchmarkProjection Projection { get; init; }
    public required BenchmarkCache Cache { get; init; }
    public required BenchmarkResources Resources { get; init; }
}

internal sealed record BenchmarkConfiguration(int Limit, int Concurrency, int Width, int Height);
internal sealed record BenchmarkTiming(double BenchmarkManagedEntryToScanInvocation, double BenchmarkManagedEntryToInitialInteractiveFrame, double? ScanInvocationToFirstModelRow, double ScanInvocationToFinalModelState, double ScanInvocationToFinalRenderedFrame, double ScanInvocationToScanDrained, double BenchmarkManagedEntryToFinalRenderedFrame);
internal sealed record BenchmarkFinalState(bool IsScanning, int ReplayRows, int TotalMatches, int TotalWins, string ScanStatus, string ScanOutcome, int AvailableReplayCount, int SelectedReplayCount, int ProcessedReplayCount, int LoadedReplayCount, int FailedReplayCount, int ObservedLimit, int ObservedConcurrency);
internal sealed record BenchmarkRender(bool CapturedFrame, int PngByteLength, bool HasVisibleContent, int NonZeroPixelBytes, int DistinctPixelByteValues);
internal sealed record BenchmarkVisualProof(int GridItemsCount, int RealizedVisibleRows, IReadOnlyList<string> AggregateTextValues);
internal sealed record BenchmarkProjection(string ReplayRowsSha256, string FrequentOpponentsSha256, int TotalMatches, int TotalWins, double WinRate, double? AvgKills, string AvgKillsDisplay, double AvgBotPercent, int IncompleteOpponentMatchCount, string OpponentDataIncompleteText);
internal sealed record BenchmarkCache(string ApplicationCacheMode, string OperatingSystemFileCacheState, CacheFileState Before, CacheFileState After, int CachedUpdates, int LoadedUpdates, int FailedUpdates);
internal sealed record BenchmarkResources(double CpuMilliseconds, long AllocatedBytes, long PeakWorkingSetBytes, long PeakPrivateBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, long AvailableMemoryBytes, int LogicalProcessors, int SamplingIntervalMilliseconds);
internal sealed record BenchmarkFailure(int SchemaVersion, string ErrorType, string Message)
{
    public static BenchmarkFailure From(Exception exception) => new(1, exception.GetType().Name, exception.Message);
}

internal static class BenchmarkJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
