using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    public bool IsSelected { get; init; }
}

public enum LibraryScanMilestone
{
    Invoked,
    FirstModelRow,
    FinalModelState,
    // Emitted only after a successful stream has been completely enumerated, disposed, and
    // the owning scan state has been cleaned up. This is the benchmark's terminal boundary.
    ScanDrained
}

/// <summary>Optional diagnostic hook for the benchmark harness; product callers need not provide one.</summary>
public interface ILibraryScanObserver
{
    void OnMilestone(LibraryScanMilestone milestone);
}

public class LibraryViewModel : ReactiveObject, IDisposable
{
    private readonly IReplayCacheService _cacheService;
    private readonly Action<ReplaySummary> _onOpenReplay;
    private readonly ISettingsService _settingsService;
    private readonly Func<ReplayScanOptions>? _scanOptionsFactory;
    private readonly ILibraryScanObserver? _scanObserver;
    private readonly TimeProvider _timeProvider;
    private readonly object _scanLock = new();
    private readonly HashSet<string> _displayedReplayPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _activeScanCancellation;
    private int _inFlightScans;
    private long _scanGeneration;
    private bool _disposed;
    private bool _isActive = true;
    private bool _preserveRowsUntilRefreshResult;
    private ITimer? _refreshTimer;
    private long _refreshScheduleGeneration;

    private string? _directoryPath;
    private bool _isScanning;
    private int _scanProgress;
    private string? _errorMessage;
    private int _replayScanLimit;
    private string _replayScanLimitText;
    private bool _autoRefreshEnabled;
    private int _autoRefreshMinutes;
    private string _autoRefreshMinutesText;
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
    private string? _selectedOpponentStableId;
    private string? _selectedOpponentName;
    private bool _hasVisibleReplays;

    public LibraryViewModel(
        Action<ReplaySummary> onOpenReplay,
        IReplayCacheService? cacheService = null,
        ISettingsService? settingsService = null,
        Func<ReplayScanOptions>? scanOptionsFactory = null,
        ILibraryScanObserver? scanObserver = null,
        TimeProvider? timeProvider = null)
    {
        _onOpenReplay = onOpenReplay;
        _cacheService = cacheService ?? new ReplayCacheService();
        _settingsService = settingsService ?? new SettingsService();
        _scanOptionsFactory = scanOptionsFactory;
        _scanObserver = scanObserver;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var settings = _settingsService.Load();
        _directoryPath = settings.ReplayDirectory;
        _replayScanLimit = NormalizeScanLimit(settings.ReplayScanLimit);
        _replayScanLimitText = _replayScanLimit.ToString(CultureInfo.InvariantCulture);
        _autoRefreshEnabled = settings.LibraryAutoRefreshEnabled;
        _autoRefreshMinutes = NormalizeRefreshMinutes(settings.LibraryAutoRefreshMinutes);
        _autoRefreshMinutesText = _autoRefreshMinutes.ToString(CultureInfo.InvariantCulture);

        Replays = new ObservableCollection<ReplaySummary>();
        VisibleReplays = new ObservableCollection<ReplaySummary>();
        Replays.CollectionChanged += OnReplaysCollectionChanged;
        FrequentOpponents = Array.Empty<FrequentOpponent>();

        var canScan = this.WhenAnyValue(x => x.DirectoryPath,
            directory => !string.IsNullOrWhiteSpace(directory));

        // These triggers intentionally start an owned task rather than becoming disabled for
        // its duration. Every press makes a new generation and cancels the one before it.
        ScanCommand = ReactiveCommand.Create(StartScanInBackground, canScan);
        ApplyScanLimitCommand = ReactiveCommand.Create(ApplyScanLimitAndStart, canScan);
        ApplyAutoRefreshMinutesCommand = ReactiveCommand.Create(ApplyAutoRefreshMinutes);
        IncreaseAutoRefreshMinutesCommand = ReactiveCommand.Create(() => ChangeAutoRefreshMinutes(1));
        DecreaseAutoRefreshMinutesCommand = ReactiveCommand.Create(() => ChangeAutoRefreshMinutes(-1));
        ClearOpponentFilterCommand = ReactiveCommand.Create(ClearOpponentFilter);
        OpenReplayCommand = ReactiveCommand.Create<ReplaySummary>(summary => _onOpenReplay(summary));

        SetDirectoryCommand = ReactiveCommand.Create<string>(path =>
        {
            DirectoryPath = path;
            _settingsService.Update(current => current.ReplayDirectory = path);
        });

        if (!string.IsNullOrWhiteSpace(_directoryPath))
            StartScanInBackground();
        ResetRefreshSchedule();
    }

    public ObservableCollection<ReplaySummary> Replays { get; }
    public ObservableCollection<ReplaySummary> VisibleReplays { get; }
    public IReadOnlyList<FrequentOpponent> FrequentOpponents { get; private set; }

    public string? SelectedOpponentStableId
    {
        get => _selectedOpponentStableId;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedOpponentStableId, value);
            this.RaisePropertyChanged(nameof(HasOpponentFilter));
            this.RaisePropertyChanged(nameof(HasNoFilteredMatches));
            this.RaisePropertyChanged(nameof(FilterSummaryText));
        }
    }

    public string? SelectedOpponentName
    {
        get => _selectedOpponentName;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedOpponentName, value);
            this.RaisePropertyChanged(nameof(FilterSummaryText));
        }
    }

    public bool HasOpponentFilter => SelectedOpponentStableId is not null;
    public bool HasNoFilteredMatches => HasOpponentFilter && !HasVisibleReplays;
    public string FilterSummaryText =>
        $"Showing {VisibleReplays.Count} of {Replays.Count} matches with {SelectedOpponentName}";

    public bool HasVisibleReplays
    {
        get => _hasVisibleReplays;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasVisibleReplays, value);
            this.RaisePropertyChanged(nameof(HasNoFilteredMatches));
        }
    }

    public string? DirectoryPath
    {
        get => _directoryPath;
        set
        {
            if (string.Equals(_directoryPath, value, StringComparison.Ordinal))
                return;

            ClearOpponentFilter();
            this.RaiseAndSetIfChanged(ref _directoryPath, value);
            var generation = CancelActiveScan();
            ClearDisplayedResultsOnUi(generation);
            ResetRefreshSchedule();
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

    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set
        {
            if (_autoRefreshEnabled == value || _disposed)
                return;
            this.RaiseAndSetIfChanged(ref _autoRefreshEnabled, value);
            _settingsService.Update(settings => settings.LibraryAutoRefreshEnabled = value);
            ResetRefreshSchedule();
        }
    }

    public int AutoRefreshMinutes
    {
        get => _autoRefreshMinutes;
        private set => this.RaiseAndSetIfChanged(ref _autoRefreshMinutes, value);
    }

    // Invalid edits stay in the text box without replacing the last valid saved interval.
    public string AutoRefreshMinutesText
    {
        get => _autoRefreshMinutesText;
        set => this.RaiseAndSetIfChanged(ref _autoRefreshMinutesText, value);
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
    public ReactiveCommand<Unit, Unit> ApplyAutoRefreshMinutesCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseAutoRefreshMinutesCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseAutoRefreshMinutesCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearOpponentFilterCommand { get; }
    public ReactiveCommand<ReplaySummary, Unit> OpenReplayCommand { get; }
    public ReactiveCommand<string, Unit> SetDirectoryCommand { get; }

    public void ToggleOpponentFilter(FrequentOpponent opponent)
    {
        ArgumentNullException.ThrowIfNull(opponent);
        if (string.IsNullOrWhiteSpace(opponent.StableId))
            return;

        if (string.Equals(SelectedOpponentStableId, opponent.StableId, StringComparison.OrdinalIgnoreCase))
        {
            ClearOpponentFilter();
            return;
        }

        SelectedOpponentStableId = opponent.StableId;
        SelectedOpponentName = opponent.Name;
        RebuildVisibleReplays();
        UpdateStats();
    }

    private void ClearOpponentFilter()
    {
        if (!HasOpponentFilter)
            return;
        SelectedOpponentStableId = null;
        SelectedOpponentName = null;
        RebuildVisibleReplays();
        UpdateStats();
    }

    private bool MatchesSelectedOpponent(ReplaySummary replay) =>
        SelectedOpponentStableId is null || replay.Opponents.Any(opponent =>
            string.Equals(opponent.StableId, SelectedOpponentStableId, StringComparison.OrdinalIgnoreCase));

    private void OnReplaysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            for (var offset = 0; offset < e.NewItems.Count; offset++)
            {
                var replay = (ReplaySummary)e.NewItems[offset]!;
                if (!MatchesSelectedOpponent(replay))
                    continue;
                var sourceIndex = e.NewStartingIndex + offset;
                var visibleIndex = Replays.Take(sourceIndex).Count(MatchesSelectedOpponent);
                VisibleReplays.Insert(visibleIndex, replay);
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
            VisibleReplays.Clear();
        else
            RebuildVisibleReplays();

        this.RaisePropertyChanged(nameof(FilterSummaryText));
    }

    private void RebuildVisibleReplays()
    {
        VisibleReplays.Clear();
        foreach (var replay in Replays.Where(MatchesSelectedOpponent))
            VisibleReplays.Add(replay);
        this.RaisePropertyChanged(nameof(FilterSummaryText));
    }

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

    private void ApplyAutoRefreshMinutes()
    {
        if (!int.TryParse(AutoRefreshMinutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            minutes is < 1 or > AppSettings.MaxLibraryAutoRefreshMinutes)
        {
            ErrorMessage = $"Auto refresh interval must be a whole number from 1 to {AppSettings.MaxLibraryAutoRefreshMinutes} minutes.";
            return;
        }

        SetAutoRefreshMinutes(minutes);
        ErrorMessage = null;
    }

    private void ChangeAutoRefreshMinutes(int delta) =>
        SetAutoRefreshMinutes(Math.Clamp(AutoRefreshMinutes + delta, 1, AppSettings.MaxLibraryAutoRefreshMinutes));

    private void SetAutoRefreshMinutes(int minutes)
    {
        if (_disposed)
            return;
        AutoRefreshMinutesText = minutes.ToString(CultureInfo.InvariantCulture);
        if (AutoRefreshMinutes == minutes)
            return;
        AutoRefreshMinutes = minutes;
        _settingsService.Update(settings => settings.LibraryAutoRefreshMinutes = minutes);
        ResetRefreshSchedule();
    }

    public void SetActive(bool active)
    {
        if (_disposed || _isActive == active)
            return;
        _isActive = active;
        ResetRefreshSchedule();
    }

    private void ResetRefreshSchedule()
    {
        var generation = Interlocked.Increment(ref _refreshScheduleGeneration);
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        if (_disposed || !_isActive || !AutoRefreshEnabled || string.IsNullOrWhiteSpace(DirectoryPath))
            return;

        _refreshTimer = _timeProvider.CreateTimer(_ =>
            Dispatcher.UIThread.Post(() => OnRefreshDue(generation)), null,
            TimeSpan.FromMinutes(AutoRefreshMinutes), Timeout.InfiniteTimeSpan);
    }

    private void OnRefreshDue(long generation)
    {
        if (_disposed || generation != Volatile.Read(ref _refreshScheduleGeneration))
            return;

        // The cadence is measured from each due tick. If a scan is still running at the
        // next tick, that tick is skipped and no second scan waits behind it.
        ResetRefreshSchedule();
        // Automatic scans reserve the idle scan slot atomically. Manual scans retain their
        // existing cancel-and-replace behavior.
        _ = ObserveScanAsync(StartScanAsync(automatic: true));
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

    private async Task StartScanAsync(bool automatic = false)
    {
        var directory = DirectoryPath;
        if (string.IsNullOrWhiteSpace(directory) || _disposed)
            return;

        var scan = automatic ? TryBeginAutomaticScan() : BeginScan();
        if (scan is null)
            return;
        var (generation, cancellation) = scan.Value;
        MarkScanMilestone(LibraryScanMilestone.Invoked);
        var pendingUpdates = new List<ReplayScanUpdate>(25);
        var batchStopwatch = Stopwatch.StartNew();
        var firstReplayPublished = false;
        var completedUpdateSeen = false;
        IAsyncEnumerator<ReplayScanUpdate>? enumerator = null;

        try
        {
            await OnUiThreadAsync(() => BeginDisplayedScan(generation, automatic));
            var scanOptions = (_scanOptionsFactory?.Invoke() ?? new ReplayScanOptions()) with
            {
                Limit = ReplayScanLimit
            };
            enumerator = _cacheService.ScanAsync(directory, scanOptions, cancellation.Token)
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
                    completedUpdateSeen |= update.IsComplete;
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
            try
            {
                if (enumerator is not null)
                {
                    try
                    {
                        await enumerator.DisposeAsync();
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }

                await OnUiThreadAsync(() =>
                {
                    if (generation == Volatile.Read(ref _scanGeneration))
                        IsScanning = false;
                });
            }
            finally
            {
                lock (_scanLock)
                {
                    _inFlightScans--;
                    if (generation == _scanGeneration && ReferenceEquals(_activeScanCancellation, cancellation))
                        _activeScanCancellation = null;
                }
                cancellation.Dispose();
            }

            if (completedUpdateSeen && generation == Volatile.Read(ref _scanGeneration) && !_disposed)
                MarkScanMilestone(LibraryScanMilestone.ScanDrained);
        }
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginScan()
    {
        lock (_scanLock)
        {
            _activeScanCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _activeScanCancellation = cancellation;
            _inFlightScans++;
            return (Interlocked.Increment(ref _scanGeneration), cancellation);
        }
    }

    private (long Generation, CancellationTokenSource Cancellation)? TryBeginAutomaticScan()
    {
        lock (_scanLock)
        {
            if (_disposed || !_isActive || !_autoRefreshEnabled || _inFlightScans != 0)
                return null;
            var cancellation = new CancellationTokenSource();
            _activeScanCancellation = cancellation;
            _inFlightScans++;
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

    private void BeginDisplayedScan(long generation, bool automatic)
    {
        if (generation != Volatile.Read(ref _scanGeneration))
            return;

        _preserveRowsUntilRefreshResult = automatic;
        if (!automatic)
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
            RaiseScanCountProperties();
        }
        ErrorMessage = null;
        IsScanning = true;
    }

    private void ClearDisplayedResults()
    {
        _preserveRowsUntilRefreshResult = false;
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
        var completed = false;
        foreach (var update in updates)
        {
            if (_preserveRowsUntilRefreshResult && update.Status == ReplayScanStatus.Started)
                continue;
            if (_preserveRowsUntilRefreshResult &&
                (update.Status is ReplayScanStatus.Cached or ReplayScanStatus.Loaded or ReplayScanStatus.Completed))
            {
                Replays.Clear();
                _displayedReplayPaths.Clear();
                UpdateStats();
                _preserveRowsUntilRefreshResult = false;
            }
            AvailableReplayCount = update.AvailableCount;
            SelectedReplayCount = update.SelectedCount;
            ProcessedReplayCount = update.ProcessedCount;
            LoadedReplayCount = update.LoadedCount;
            FailedReplayCount = update.FailedCount;
            ScanProgress = update.ProgressPercentage;

            if (update.Status is ReplayScanStatus.Cached or ReplayScanStatus.Loaded)
            {
                var added = AddReplayIfNew(update.Summary!);
                statsChanged |= added;
                if (added && Replays.Count == 1)
                    MarkScanMilestone(LibraryScanMilestone.FirstModelRow);
            }

            if (update.Status == ReplayScanStatus.Failed && !string.IsNullOrWhiteSpace(update.ErrorMessage))
            ErrorMessage = "Some replay files could not be loaded. See the loaded and failed counts above.";

            if (update.IsComplete)
            {
                IsScanning = false;
                completed = true;
            }
        }

        RaiseScanCountProperties();
        if (statsChanged)
            UpdateStats();
        if (completed)
            MarkScanMilestone(LibraryScanMilestone.FinalModelState);
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
    private static int NormalizeRefreshMinutes(int value) =>
        value is >= 1 and <= AppSettings.MaxLibraryAutoRefreshMinutes
            ? value
            : AppSettings.DefaultLibraryAutoRefreshMinutes;

    private void MarkScanMilestone(LibraryScanMilestone milestone)
    {
        try { _scanObserver?.OnMilestone(milestone); }
        catch { /* Diagnostics must not affect scanning. */ }
    }

    private void UpdateStats()
    {
        TotalMatches = VisibleReplays.Count;
        HasReplays = Replays.Count > 0;
        HasVisibleReplays = VisibleReplays.Count > 0;
        this.RaisePropertyChanged(nameof(FilterSummaryText));
        TotalWins = VisibleReplays.Count(replay => replay.IsWin);
        IncompleteOpponentMatchCount = VisibleReplays.Count(replay => !replay.OpponentAnalysisComplete);
        this.RaisePropertyChanged(nameof(HasIncompleteOpponentData));
        this.RaisePropertyChanged(nameof(OpponentDataIncompleteText));
        WinRate = TotalMatches > 0 ? (double)TotalWins / TotalMatches * 100 : 0;
        var knownKillCounts = VisibleReplays.Select(replay => replay.Kills).OfType<int>().ToList();
        AvgKills = knownKillCounts.Count > 0 ? knownKillCounts.Average() : null;
        this.RaisePropertyChanged(nameof(AvgKillsDisplay));
        AvgBotPercent = TotalMatches > 0 ? VisibleReplays.Average(replay => replay.BotPercent) : 0;

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
                Appearances = group.Select(entry => entry.MatchIndex).Distinct().Count(),
                IsSelected = string.Equals(group.Key, SelectedOpponentStableId, StringComparison.OrdinalIgnoreCase)
            })
            .OrderByDescending(opponent => opponent.Appearances)
            .ThenBy(opponent => opponent.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(opponent => opponent.Name, StringComparer.Ordinal)
            .ThenBy(opponent => opponent.StableId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(opponent => opponent.StableId, StringComparer.Ordinal)
            .Take(10)
            .ToList();
        if (SelectedOpponentStableId is not null)
        {
            var latestSelectedName = Replays
                .SelectMany(replay => replay.Opponents
                    .Where(opponent => string.Equals(opponent.StableId, SelectedOpponentStableId,
                        StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(opponent.Name))
                    .Select(opponent => new { opponent.Name, replay.FileDate }))
                .OrderByDescending(entry => entry.FileDate)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => entry.Name)
                .FirstOrDefault();
            if (latestSelectedName is not null && SelectedOpponentName != latestSelectedName)
                SelectedOpponentName = latestSelectedName;
        }
        this.RaisePropertyChanged(nameof(FrequentOpponents));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Replays.CollectionChanged -= OnReplaysCollectionChanged;
        ResetRefreshSchedule();
        CancelActiveScan();
    }
}
