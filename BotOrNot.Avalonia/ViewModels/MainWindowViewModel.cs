using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using BotOrNot.Avalonia.Services;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;
using ReactiveUI;

namespace BotOrNot.Avalonia.ViewModels;

public class MainWindowViewModel : ReactiveObject
{
    private readonly IReplayService _replayService;
    private ThemePreference _currentTheme;

    private static readonly string AppVersion = (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    private static readonly string BaseTitle = $"Bot or Not? v{AppVersion}";

    private ObservableCollection<PlayerRow> _players = new();
    private ObservableCollection<PlayerRow> _ownerEliminations = new();
    private string _ownerKillsHeader = "Your Eliminations";
    private string _playersSeenHeader = "Players Seen";
    private bool _isLoading;
    private string? _errorMessage;
    private string _filterText = "";
    private string _windowTitle = BaseTitle;
    private string? _gameMode;
    private string? _placementText;
    private string? _durationText;
    private string? _elimsSummary;
    private string? _playlistName;
    private bool _hasMetadata;
    private bool _hasData;
    private bool _isDropTargetActive;
    private string _themeIcon = "\u2699";
    private string _themeToggleTooltip = "Theme: System";

    private readonly List<PlayerRow> _allPlayers = new();
    private readonly List<PlayerRow> _allOwnerEliminations = new();

    private ObservableCollection<PlayerRow> _filteredPlayers = new();
    private ObservableCollection<PlayerRow> _filteredOwnerEliminations = new();

    private string? _eliminatorName;

    public MainWindowViewModel()
    {
        _replayService = new ReplayService();

        // Load saved theme preference and apply before window renders
        var settings = SettingsService.Load();
        _currentTheme = settings.Theme;
        ApplyTheme();

        LoadReplayCommand = ReactiveCommand.CreateFromTask<string>(LoadReplayAsync);
        LoadReplayCommand.ThrownExceptions.Subscribe(ex =>
        {
            ErrorMessage = $"Failed to load replay: {ex.Message}";
            IsLoading = false;
        });

        CycleThemeCommand = ReactiveCommand.Create(CycleTheme);
        FilterByPlayerCommand = ReactiveCommand.Create<string>(FilterByPlayer);

        // Filter logic - react to filter text changes
        this.WhenAnyValue(x => x.FilterText)
            .Subscribe(_ => ApplyFilter());
    }

    public ObservableCollection<PlayerRow> Players => _filteredPlayers;

    public ObservableCollection<PlayerRow> OwnerEliminations => _filteredOwnerEliminations;

    public string OwnerKillsHeader
    {
        get => _ownerKillsHeader;
        set => this.RaiseAndSetIfChanged(ref _ownerKillsHeader, value);
    }

    public string PlayersSeenHeader
    {
        get => _playersSeenHeader;
        set => this.RaiseAndSetIfChanged(ref _playersSeenHeader, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set => this.RaiseAndSetIfChanged(ref _windowTitle, value);
    }

    public string? GameMode
    {
        get => _gameMode;
        set => this.RaiseAndSetIfChanged(ref _gameMode, value);
    }

    public string? PlaylistName
    {
        get => _playlistName;
        set => this.RaiseAndSetIfChanged(ref _playlistName, value);
    }

    public string? PlacementText
    {
        get => _placementText;
        set => this.RaiseAndSetIfChanged(ref _placementText, value);
    }

    public string? DurationText
    {
        get => _durationText;
        set => this.RaiseAndSetIfChanged(ref _durationText, value);
    }

    public string? ElimsSummary
    {
        get => _elimsSummary;
        set => this.RaiseAndSetIfChanged(ref _elimsSummary, value);
    }

    public bool HasMetadata
    {
        get => _hasMetadata;
        set => this.RaiseAndSetIfChanged(ref _hasMetadata, value);
    }

    public bool HasData
    {
        get => _hasData;
        set => this.RaiseAndSetIfChanged(ref _hasData, value);
    }

    public bool IsDropTargetActive
    {
        get => _isDropTargetActive;
        set => this.RaiseAndSetIfChanged(ref _isDropTargetActive, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public string FilterText
    {
        get => _filterText;
        set => this.RaiseAndSetIfChanged(ref _filterText, value);
    }

    public ReactiveCommand<string, Unit> LoadReplayCommand { get; }

    public string? EliminatorName
    {
        get => _eliminatorName;
        set => this.RaiseAndSetIfChanged(ref _eliminatorName, value);
    }

    public ReactiveCommand<string, Unit> FilterByPlayerCommand { get; }

    public ReactiveCommand<Unit, Unit> CycleThemeCommand { get; }

    public string ThemeIcon
    {
        get => _themeIcon;
        set => this.RaiseAndSetIfChanged(ref _themeIcon, value);
    }

    public string ThemeToggleTooltip
    {
        get => _themeToggleTooltip;
        set => this.RaiseAndSetIfChanged(ref _themeToggleTooltip, value);
    }

    private void CycleTheme()
    {
        _currentTheme = _currentTheme switch
        {
            ThemePreference.System => ThemePreference.Light,
            ThemePreference.Light => ThemePreference.Dark,
            ThemePreference.Dark => ThemePreference.System,
            _ => ThemePreference.System
        };

        ApplyTheme();
        SettingsService.Save(new AppSettings { Theme = _currentTheme });
    }

    private void ApplyTheme()
    {
        if (global::Avalonia.Application.Current != null)
        {
            global::Avalonia.Application.Current.RequestedThemeVariant = SettingsService.ToThemeVariant(_currentTheme);
        }

        (ThemeIcon, ThemeToggleTooltip) = _currentTheme switch
        {
            ThemePreference.Light => ("\u2600", "Theme: Light"),
            ThemePreference.Dark => ("\uD83C\uDF19", "Theme: Dark"),
            _ => ("\u2699", "Theme: System")
        };
    }

    private void FilterByPlayer(string playerName)
    {
        FilterText = FilterText.Equals(playerName, StringComparison.OrdinalIgnoreCase) ? "" : playerName;
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            _filteredPlayers.Clear();
            _filteredOwnerEliminations.Clear();
            foreach (var p in _allPlayers) _filteredPlayers.Add(p);
            foreach (var p in _allOwnerEliminations) _filteredOwnerEliminations.Add(p);
        }
        else
        {
            var searchTerm = FilterText.ToLowerInvariant();
            var filteredPlayersList = _allPlayers.Where(p => MatchesFilter(p, searchTerm)).ToList();
            var filteredOwnerElimList = _allOwnerEliminations.Where(p => MatchesFilter(p, searchTerm)).ToList();

            _filteredPlayers.Clear();
            _filteredOwnerEliminations.Clear();
            foreach (var p in filteredPlayersList) _filteredPlayers.Add(p);
            foreach (var p in filteredOwnerElimList) _filteredOwnerEliminations.Add(p);
        }
    }

    private static bool MatchesFilter(PlayerRow player, string searchTerm)
    {
        return (player.Name?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.Level?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               PlatformHelper.GetFriendlyName(player.Platform).Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
               (player.Kills?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.TeamIndex?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.Placement?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.DeathCause?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.ElimTime?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task LoadReplayAsync(string path)
    {
        ErrorMessage = null;
        IsLoading = true;

        try
        {
            var data = await _replayService.LoadReplayAsync(path);

            _allPlayers.Clear();
            _allOwnerEliminations.Clear();
            // Sort by ElimTime (chronological) by default; players without ElimTime go to the bottom
            foreach (var p in data.Players.OrderBy(p => string.IsNullOrEmpty(p.ElimTime) ? 1 : 0).ThenBy(p => p.ElimTime, StringComparer.Ordinal))
                _allPlayers.Add(p);
            foreach (var p in data.OwnerEliminations.OrderBy(p => string.IsNullOrEmpty(p.ElimTime) ? 1 : 0).ThenBy(p => p.ElimTime, StringComparer.Ordinal))
                _allOwnerEliminations.Add(p);

            ApplyFilter();

            var ownerDisplay = !string.IsNullOrEmpty(data.OwnerName) ? data.OwnerName : "Your";
            // Use authoritative kill count from PlayerData, fall back to event-based count
            var nonNpcEliminations = data.OwnerEliminations.Where(p => !p.IsNpc).ToList();
            var totalKills = data.OwnerKills ?? nonNpcEliminations.Count;
            var botKills = nonNpcEliminations.Count(p => p.IsBot);
            var playerKills = totalKills - botKills;
            OwnerKillsHeader = $"{ownerDisplay}'s Eliminations ({totalKills}) - {playerKills} Players, {botKills} Bots";

            // Build Players Seen header with breakdown (excluding NPCs)
            var npcCount = data.Players.Count(p => p.IsNpc);
            var nonNpcPlayers = data.Players.Where(p => !p.IsNpc).ToList();
            var totalPlayers = nonNpcPlayers.Count;
            var botPlayers = nonNpcPlayers.Count(p => p.IsBot);
            var humanPlayers = totalPlayers - botPlayers;

            // Group by platform (using friendly names) and build platform breakdown string (excluding NPCs)
            var platformGroups = nonNpcPlayers
                .Where(p => !string.IsNullOrWhiteSpace(p.Platform))
                .GroupBy(p => PlatformHelper.GetFriendlyName(p.Platform))
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();

            var platformBreakdown = platformGroups.Count > 0 ? " | " + string.Join(", ", platformGroups) : "";
            var npcPrefix = npcCount > 0 ? $"NPCs Seen ({npcCount}) | " : "";
            PlayersSeenHeader = $"{npcPrefix}Players Seen ({totalPlayers}) - {humanPlayers} Players, {botPlayers} Bots{platformBreakdown}";

            // Find owner's placement
            var ownerPlayer = data.Players.FirstOrDefault(p =>
                !string.IsNullOrEmpty(data.OwnerName) &&
                p.Name?.Equals(data.OwnerName, StringComparison.OrdinalIgnoreCase) == true);
            var ownerPlacement = ownerPlayer?.Placement;
            var placementText = !string.IsNullOrEmpty(ownerPlacement)
                ? $"Placement: #{ownerPlacement}"
                : "Placement: Unknown";

            // Extract eliminator name for clickable link (only when we know they didn't win)
            // Note: Only shows eliminator when owner placement is known (not null).
            // Original behavior showed eliminator even when placement was unknown; this change
            // is intentional to avoid showing misleading information when owner wasn't found.
            EliminatorName = ownerPlacement is not null and not "1" && data.OwnerEliminatedBy != null
                ? data.OwnerEliminatedBy
                : null;

            // Set individual header bar segments
            WindowTitle = $"{BaseTitle} - {data.Metadata.FileName}";
            GameMode = data.Metadata.GameMode;
            PlaylistName = data.Metadata.Playlist;
            PlacementText = !string.IsNullOrEmpty(ownerPlacement) ? $"#{ownerPlacement}" : "?";
            DurationText = $"{data.Metadata.MatchDurationMinutes:F1}m";
            ElimsSummary = $"{totalKills} Elims ({botKills} Bot{(botKills != 1 ? "s" : "")})";
            HasMetadata = true;
            HasData = true;
        }
        catch (IOException ex)
        {
            ErrorMessage = $"Could not read replay file: {ex.Message} (The file may still be locked by Fortnite.)";
        }
        catch (IndexOutOfRangeException)
        {
            ErrorMessage = "This replay is from a newer version of Fortnite that isn't supported yet. Please check for an app update.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load replay: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
