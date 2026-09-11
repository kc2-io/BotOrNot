using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reactive;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using BotOrNot.Avalonia.Services;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;
using ReactiveUI;

namespace BotOrNot.Avalonia.ViewModels;

public sealed class FrequentOpponent
{
    public string StableId { get; init; } = "";
    public string Name { get; init; } = "";
    public int Appearances { get; init; }
}

public class LibraryViewModel : ReactiveObject, IDisposable
{
    private readonly IReplayCacheService _cacheService;
    private readonly Action<ReplaySummary> _onOpenReplay;
    private readonly ISettingsService _settingsService;
    private readonly object _scanLock = new();
    private readonly HashSet<string> _displayedReplayPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _activeScanCancellation;
    private long _scanGeneration;
    private bool _disposed;

    private string? _directoryPath;
    private bool _isScanning;
    private int _scanProgress;
    private string? _errorMessage;
    private int _replayScanLimit;
    private string _replayScanLimitText;
    private int _availableReplayCount;
    private int _selectedReplayCount;
    private int _processedReplayCount;
    private int _loadedReplayCount;
    private int _failedReplayCount;
    private int _totalMatches;
    private int _totalWins;
    private double _winRate;
    private double? _avgKills;
    private double _avgBotPercent;
    private bool _hasReplays;
    private int _incompleteOpponentMatchCount;

    public LibraryViewModel(
        Action<ReplaySummary> onOpenReplay,
        IReplayCacheService? cacheService = null,
        ISettingsService? settingsService = null)
    {
        _onOpenReplay = onOpenReplay;
        _cacheService = cacheService ?? new ReplayCacheService();
        _settingsService = settingsService ?? new SettingsService();

        var settings = _settingsService.Load();
        _directoryPath = settings.ReplayDirectory;
        _replayScanLimit = NormalizeScanLimit(settings.ReplayScanLimit);
        _replayScanLimitText = _replayScanLimit.ToString(CultureInfo.InvariantCulture);

        Replays = new ObservableCollection<ReplaySummary>();
        FrequentOpponents = Array.Empty<FrequentOpponent>();

        var canScan = this.WhenAnyValue(x => x.DirectoryPath,
            directory => !string.IsNullOrWhiteSpace(directory));

        // These triggers intentionally start an owned task rather than becoming disabled for
        // its duration. Every press makes a new generation and cancels the one before it.
        ScanCommand = ReactiveCommand.Create(StartScanInBackground, canScan);
        ApplyScanLimitCommand = ReactiveCommand.Create(ApplyScanLimitAndStart, canScan);
        OpenReplayCommand = ReactiveCommand.Create<ReplaySummary>(summary => _onOpenReplay(summary));

        SetDirectoryCommand = ReactiveCommand.Create<string>(path =>
        {
            DirectoryPath = path;
            _settingsService.Update(current => current.ReplayDirectory = path);
        });

        if (!string.IsNullOrWhiteSpace(_directoryPath))
            StartScanInBackground();
    }

    public ObservableCollection<ReplaySummary> Replays { get; }
    public IReadOnlyList<FrequentOpponent> FrequentOpponents { get; private set; }

    public string? DirectoryPath
    {
        get => _directoryPath;
        set
        {
            if (string.Equals(_directoryPath, value, StringComparison.Ordinal))
                return;

            this.RaiseAndSetIfChanged(ref _directoryPath, value);
            var generation = CancelActiveScan();
            ClearDisplayedResultsOnUi(generation);
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => this.RaiseAndSetIfChanged(ref _isScanning, value);
    }

    public int ScanProgress
    {
        get => _scanProgress;
        private set => this.RaiseAndSetIfChanged(ref _scanProgress, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public int ReplayScanLimit
    {
        get => _replayScanLimit;
        private set => this.RaiseAndSetIfChanged(ref _replayScanLimit, value);
    }

    // Keep typed text separate from the applied value so an invalid edit never replaces a
    // saved, working scan limit.
    public string ReplayScanLimitText
    {
        get => _replayScanLimitText;
        set => this.RaiseAndSetIfChanged(ref _replayScanLimitText, value);
    }

    public int AvailableReplayCount
    {
        get => _availableReplayCount;
        private set => this.RaiseAndSetIfChanged(ref _availableReplayCount, value);
    }

    public int SelectedReplayCount
    {
        get => _selectedReplayCount;
        private set => this.RaiseAndSetIfChanged(ref _selectedReplayCount, value);
    }

    public int ProcessedReplayCount
    {
        get => _processedReplayCount;
        private set => this.RaiseAndSetIfChanged(ref _processedReplayCount, value);
    }

    public int LoadedReplayCount
    {
        get => _loadedReplayCount;
        private set => this.RaiseAndSetIfChanged(ref _loadedReplayCount, value);
    }

    public int FailedReplayCount
    {
        get => _failedReplayCount;
        private set => this.RaiseAndSetIfChanged(ref _failedReplayCount, value);
    }

    public string ScanStatusText => $"Scanned {ProcessedReplayCount} out of {AvailableReplayCount} replay files";

    public string ScanOutcomeText => FailedReplayCount == 0
        ? $"{LoadedReplayCount} loaded"
        : $"{LoadedReplayCount} loaded, {FailedReplayCount} failed";

    public int TotalMatches
    {
        get => _totalMatches;
        private set => this.RaiseAndSetIfChanged(ref _totalMatches, value);
    }

    public int TotalWins
    {
        get => _totalWins;
        private set => this.RaiseAndSetIfChanged(ref _totalWins, value);
    }

    public double WinRate
    {
        get => _winRate;
        private set => this.RaiseAndSetIfChanged(ref _winRate, value);
    }

    public double? AvgKills
    {
        get => _avgKills;
        private set => this.RaiseAndSetIfChanged(ref _avgKills, value);
    }

    public string AvgKillsDisplay => AvgKills.HasValue ? $"{AvgKills.Value:F1}" : "Unknown";

    public double AvgBotPercent
    {
        get => _avgBotPercent;
        private set => this.RaiseAndSetIfChanged(ref _avgBotPercent, value);
    }

    public bool HasReplays
    {
        get => _hasReplays;
        private set => this.RaiseAndSetIfChanged(ref _hasReplays, value);
    }

    public int IncompleteOpponentMatchCount
    {
        get => _incompleteOpponentMatchCount;
        private set => this.RaiseAndSetIfChanged(ref _incompleteOpponentMatchCount, value);
    }

    public bool HasIncompleteOpponentData => IncompleteOpponentMatchCount > 0;

    public string OpponentDataIncompleteText =>
        $"Opponent data incomplete for {IncompleteOpponentMatchCount} {(IncompleteOpponentMatchCount == 1 ? "match" : "matches")}";

    public ReactiveCommand<Unit, Unit> ScanCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyScanLimitCommand { get; }
    public ReactiveCommand<ReplaySummary, Unit> OpenReplayCommand { get; }
    public ReactiveCommand<string, Unit> SetDirectoryCommand { get; }

    private void ApplyScanLimitAndStart()
    {
        if (!int.TryParse(ReplayScanLimitText, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
        {
            ErrorMessage = "Replay scan limit must be a positive whole number.";
            return;
        }

        ReplayScanLimit = limit;
        ReplayScanLimitText = limit.ToString(CultureInfo.InvariantCulture);
        _settingsService.Update(settings => settings.ReplayScanLimit = limit);
        StartScanInBackground();
    }

    private void StartScanInBackground() => _ = ObserveScanAsync(StartScanAsync());

    // Keep every background task observed even if a dispatcher implementation or a third-party
    // stream unexpectedly faults outside StartScanAsync's scan-error handling.
    private static async Task ObserveScanAsync(Task scan)
    {
        try
        {
            await scan;
        }
        catch
        {
            // StartScanAsync publishes operational errors itself. This final observation keeps
            // an unexpected dispatcher or stream failure from escaping a fire-and-forget task.
        }
    }

    private async Task StartScanAsync()
    {
        var directory = DirectoryPath;
        if (string.IsNullOrWhiteSpace(directory) || _disposed)
            return;

        var (generation, cancellation) = BeginScan();
        var pendingUpdates = new List<ReplayScanUpdate>(25);
        var batchStopwatch = Stopwatch.StartNew();
        var firstReplayPublished = false;

        try
        {
            await OnUiThreadAsync(() => BeginDisplayedScan(generation));
            await using var enumerator = _cacheService.ScanAsync(directory,
                new ReplayScanOptions { Limit = ReplayScanLimit }, cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            Task<bool>? next = null;
            try
            {
                while (true)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    next ??= enumerator.MoveNextAsync().AsTask();
                    // Flush a small cached batch even while the next decode is still waiting.
                    // Checking elapsed time only when a new item arrives can strand cached rows.
                    if (pendingUpdates.Count > 0)
                    {
                        var remaining = Math.Max(1, 75 - batchStopwatch.ElapsedMilliseconds);
                        if (await Task.WhenAny(next, Task.Delay((int)remaining, cancellation.Token)) != next)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            await FlushUpdatesAsync(generation, pendingUpdates);
                            batchStopwatch.Restart();
                            continue;
                        }
                    }
                    if (!await next)
                        break;
                    next = null;
                    if (generation != Volatile.Read(ref _scanGeneration) || cancellation.IsCancellationRequested)
                        break;

                    var update = enumerator.Current;
                    pendingUpdates.Add(update);
                    var firstReplay = !firstReplayPublished &&
                        (update.Status is ReplayScanStatus.Cached or ReplayScanStatus.Loaded);
                    if (update.Status == ReplayScanStatus.Started || firstReplay ||
                        pendingUpdates.Count >= 25 || batchStopwatch.ElapsedMilliseconds >= 75 || update.IsComplete)
                    {
                        await FlushUpdatesAsync(generation, pendingUpdates);
                        firstReplayPublished |= firstReplay;
                        batchStopwatch.Restart();
                    }
                }
            }
            finally
            {
                // Async iterators cannot be disposed while MoveNextAsync is still active.
                if (next is not null)
                {
                    try { await next; }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
            }

            await FlushUpdatesAsync(generation, pendingUpdates);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await OnUiThreadAsync(() =>
            {
                if (generation == Volatile.Read(ref _scanGeneration))
                    ErrorMessage = $"Scan failed: {exception.Message}";
            });
        }
        finally
        {
            await OnUiThreadAsync(() =>
            {
                if (generation == Volatile.Read(ref _scanGeneration))
                    IsScanning = false;
            });

            lock (_scanLock)
            {
                if (generation == _scanGeneration && ReferenceEquals(_activeScanCancellation, cancellation))
                    _activeScanCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginScan()
    {
        lock (_scanLock)
        {
            _activeScanCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _activeScanCancellation = cancellation;
            return (Interlocked.Increment(ref _scanGeneration), cancellation);
        }
    }

    private long CancelActiveScan()
    {
        lock (_scanLock)
        {
            var generation = Interlocked.Increment(ref _scanGeneration);
            _activeScanCancellation?.Cancel();
            _activeScanCancellation = null;
            return generation;
        }
    }

    private void ClearDisplayedResultsOnUi(long generation)
    {
        void ClearIfCurrent()
        {
            if (generation == Volatile.Read(ref _scanGeneration))
                ClearDisplayedResults();
        }

        if (Dispatcher.UIThread.CheckAccess())
            ClearIfCurrent();
        else
            Dispatcher.UIThread.Post(ClearIfCurrent);
    }

    private void BeginDisplayedScan(long generation)
    {
        if (generation != Volatile.Read(ref _scanGeneration))
            return;

        Replays.Clear();
        _displayedReplayPaths.Clear();
        UpdateStats();
        AvailableReplayCount = 0;
        SelectedReplayCount = 0;
        ProcessedReplayCount = 0;
        LoadedReplayCount = 0;
        FailedReplayCount = 0;
        ScanProgress = 0;
        ErrorMessage = null;
        IsScanning = true;
        RaiseScanCountProperties();
    }

    private void ClearDisplayedResults()
    {
        Replays.Clear();
        _displayedReplayPaths.Clear();
        UpdateStats();
        AvailableReplayCount = 0;
        SelectedReplayCount = 0;
        ProcessedReplayCount = 0;
        LoadedReplayCount = 0;
        FailedReplayCount = 0;
        ScanProgress = 0;
        IsScanning = false;
        RaiseScanCountProperties();
    }

    private async Task FlushUpdatesAsync(long generation, List<ReplayScanUpdate> pendingUpdates)
    {
        if (pendingUpdates.Count == 0)
            return;

        var updates = pendingUpdates.ToArray();
        pendingUpdates.Clear();
        await OnUiThreadAsync(() => ApplyScanUpdates(generation, updates));
    }

    private void ApplyScanUpdates(long generation, IReadOnlyList<ReplayScanUpdate> updates)
    {
        if (generation != Volatile.Read(ref _scanGeneration) || updates.Count == 0)
            return;

        var statsChanged = false;
        foreach (var update in updates)
        {
            AvailableReplayCount = update.AvailableCount;
            SelectedReplayCount = update.SelectedCount;
            ProcessedReplayCount = update.ProcessedCount;
            LoadedReplayCount = update.LoadedCount;
            FailedReplayCount = update.FailedCount;
            ScanProgress = update.ProgressPercentage;

            if (update.Status is ReplayScanStatus.Cached or ReplayScanStatus.Loaded)
                statsChanged |= AddReplayIfNew(update.Summary!);

            if (update.Status == ReplayScanStatus.Failed && !string.IsNullOrWhiteSpace(update.ErrorMessage))
            ErrorMessage = "Some replay files could not be loaded. See the loaded and failed counts above.";

            if (update.IsComplete)
                IsScanning = false;
        }

        RaiseScanCountProperties();
        if (statsChanged)
            UpdateStats();
    }

    private bool AddReplayIfNew(ReplaySummary summary)
    {
        var key = ReplayKey(summary);
        if (!_displayedReplayPaths.Add(key))
            return false;

        var index = 0;
        while (index < Replays.Count && CompareReplayOrder(Replays[index], summary) <= 0)
            index++;
        Replays.Insert(index, summary);
        return true;
    }

    private static int CompareReplayOrder(ReplaySummary left, ReplaySummary right)
    {
        var dateComparison = right.FileDate.CompareTo(left.FileDate);
        if (dateComparison != 0)
            return dateComparison;

        var ignoreCase = StringComparer.OrdinalIgnoreCase.Compare(ReplayKey(left), ReplayKey(right));
        return ignoreCase != 0
            ? ignoreCase
            : StringComparer.Ordinal.Compare(ReplayKey(left), ReplayKey(right));
    }

    private static string ReplayKey(ReplaySummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.FilePath))
            return Path.GetFullPath(summary.FilePath);
        if (!string.IsNullOrWhiteSpace(summary.FileName))
            return $"file:{summary.FileName}";
        return $"summary:{RuntimeHelpers.GetHashCode(summary)}";
    }

    private void RaiseScanCountProperties()
    {
        this.RaisePropertyChanged(nameof(ScanStatusText));
        this.RaisePropertyChanged(nameof(ScanOutcomeText));
    }

    private static async Task OnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static int NormalizeScanLimit(int value) => value > 0 ? value : AppSettings.DefaultReplayScanLimit;

    private void UpdateStats()
    {
        TotalMatches = Replays.Count;
        HasReplays = Replays.Count > 0;
        TotalWins = Replays.Count(replay => replay.IsWin);
        IncompleteOpponentMatchCount = Replays.Count(replay => !replay.OpponentAnalysisComplete);
        this.RaisePropertyChanged(nameof(HasIncompleteOpponentData));
        this.RaisePropertyChanged(nameof(OpponentDataIncompleteText));
        WinRate = TotalMatches > 0 ? (double)TotalWins / TotalMatches * 100 : 0;
        var knownKillCounts = Replays.Select(replay => replay.Kills).OfType<int>().ToList();
        AvgKills = knownKillCounts.Count > 0 ? knownKillCounts.Average() : null;
        this.RaisePropertyChanged(nameof(AvgKillsDisplay));
        AvgBotPercent = TotalMatches > 0 ? Replays.Average(replay => replay.BotPercent) : 0;

        FrequentOpponents = Replays
            .SelectMany((replay, matchIndex) => replay.Opponents
                .Where(opponent => !string.IsNullOrWhiteSpace(opponent.StableId))
                .GroupBy(opponent => opponent.StableId, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.Select(opponent => new
                {
                    Opponent = opponent,
                    replay.FileDate,
                    MatchIndex = matchIndex
                })))
            .GroupBy(entry => entry.Opponent.StableId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FrequentOpponent
            {
                StableId = group.Key,
                Name = group
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Opponent.Name))
                    .OrderByDescending(entry => entry.FileDate)
                    .ThenBy(entry => entry.Opponent.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Opponent.Name, StringComparer.Ordinal)
                    .Select(entry => entry.Opponent.Name)
                    .FirstOrDefault() ?? "Unknown player",
                Appearances = group.Select(entry => entry.MatchIndex).Distinct().Count()
            })
            .OrderByDescending(opponent => opponent.Appearances)
            .ThenBy(opponent => opponent.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(opponent => opponent.Name, StringComparer.Ordinal)
            .ThenBy(opponent => opponent.StableId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(opponent => opponent.StableId, StringComparer.Ordinal)
            .Take(10)
            .ToList();
        this.RaisePropertyChanged(nameof(FrequentOpponents));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelActiveScan();
    }
}
