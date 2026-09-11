using System.Collections.ObjectModel;
using System.Reactive;
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

public class LibraryViewModel : ReactiveObject
{
    private readonly IReplayCacheService _cacheService;
    private readonly Action<ReplaySummary> _onOpenReplay;

    private string? _directoryPath;
    private bool _isScanning;
    private int _scanProgress;
    private string? _errorMessage;
    private int _totalMatches;
    private int _totalWins;
    private double _winRate;
    private double? _avgKills;
    private double _avgBotPercent;
    private bool _hasReplays;

    public LibraryViewModel(Action<ReplaySummary> onOpenReplay, IReplayCacheService? cacheService = null)
    {
        _onOpenReplay = onOpenReplay;
        _cacheService = cacheService ?? new ReplayCacheService();

        var settings = SettingsService.Load();
        _directoryPath = settings.ReplayDirectory;

        Replays = new ObservableCollection<ReplaySummary>();
        FrequentOpponents = Array.Empty<FrequentOpponent>();

        var canScan = this.WhenAnyValue(
            x => x.DirectoryPath, x => x.IsScanning,
            (dir, scanning) => !string.IsNullOrEmpty(dir) && !scanning);

        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync, canScan);
        OpenReplayCommand = ReactiveCommand.Create<ReplaySummary>(s => _onOpenReplay(s));

        SetDirectoryCommand = ReactiveCommand.Create<string>(path =>
        {
            DirectoryPath = path;
            var s = SettingsService.Load();
            s.ReplayDirectory = path;
            SettingsService.Save(s);
        });

        if (!string.IsNullOrEmpty(_directoryPath))
            ScanCommand.Execute().Subscribe();
    }

    public ObservableCollection<ReplaySummary> Replays { get; }
    public IReadOnlyList<FrequentOpponent> FrequentOpponents { get; private set; }

    public string? DirectoryPath
    {
        get => _directoryPath;
        set => this.RaiseAndSetIfChanged(ref _directoryPath, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        set => this.RaiseAndSetIfChanged(ref _isScanning, value);
    }

    public int ScanProgress
    {
        get => _scanProgress;
        set => this.RaiseAndSetIfChanged(ref _scanProgress, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

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

    public ReactiveCommand<Unit, Unit> ScanCommand { get; }
    public ReactiveCommand<ReplaySummary, Unit> OpenReplayCommand { get; }
    public ReactiveCommand<string, Unit> SetDirectoryCommand { get; }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(DirectoryPath)) return;

        IsScanning = true;
        ScanProgress = 0;
        ErrorMessage = null;

        try
        {
            var progress = new Progress<int>(p => ScanProgress = p);
            var summaries = await _cacheService.GetSummariesAsync(DirectoryPath, progress, cancellationToken);
            var ordered = summaries.OrderByDescending(s => s.FileDate).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Replays.Clear();
                foreach (var s in ordered)
                    Replays.Add(s);
                UpdateStats();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void UpdateStats()
    {
        TotalMatches = Replays.Count;
        HasReplays = Replays.Count > 0;
        TotalWins = Replays.Count(r => r.IsWin);
        WinRate = TotalMatches > 0 ? (double)TotalWins / TotalMatches * 100 : 0;
        var knownKillCounts = Replays.Select(r => r.Kills).OfType<int>().ToList();
        AvgKills = knownKillCounts.Count > 0 ? knownKillCounts.Average() : null;
        this.RaisePropertyChanged(nameof(AvgKillsDisplay));
        AvgBotPercent = TotalMatches > 0 ? Replays.Average(r => r.BotPercent) : 0;

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
}
