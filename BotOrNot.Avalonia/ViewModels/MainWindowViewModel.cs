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
    private readonly Action? _onBack;
    private readonly IThemeService _themeService;

    private static readonly string AppVersion = (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    private static readonly string BaseTitle = $"Bot or Not? v{AppVersion}";

    private ObservableCollection<PlayerRow> _players = new();
    private ObservableCollection<PlayerRow> _ownerEliminations = new();
    private readonly ObservableCollection<SquadMemberSummary> _teammates = new();
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
    private string? _eliminationCoverageNotice;
    private string? _squadStatusText;
    private bool _hasSquadSection;
    private bool _hasSquadMembers;

    public MainWindowViewModel(
        Action? onBack = null,
        IReplayService? replayService = null,
        IThemeService? themeService = null)
    {
        _onBack = onBack;
        _replayService = replayService ?? new ReplayService();
        _themeService = themeService ?? new ThemeService(new SettingsService());

        _themeService.ApplySavedTheme();
        UpdateThemeDisplay();

        LoadReplayCommand = ReactiveCommand.CreateFromTask<string>(LoadReplayAsync);
        LoadReplayCommand.ThrownExceptions.Subscribe(ex =>
        {
            ErrorMessage = $"Failed to load replay: {ex.Message}";
            IsLoading = false;
        });

        CycleThemeCommand = ReactiveCommand.Create(CycleTheme);
        FilterByPlayerCommand = ReactiveCommand.Create<string>(FilterByPlayer);
        BackCommand = ReactiveCommand.Create(() => _onBack?.Invoke());

        // Filter logic - react to filter text changes
        this.WhenAnyValue(x => x.FilterText)
            .Subscribe(_ => ApplyFilter());
    }

    public ObservableCollection<PlayerRow> Players => _filteredPlayers;

    public ObservableCollection<PlayerRow> OwnerEliminations => _filteredOwnerEliminations;

    /// <summary>Observed teammates, kept separate from the opponent filter and grids.</summary>
    public ObservableCollection<SquadMemberSummary> Teammates => _teammates;

    public string? SquadStatusText
    {
        get => _squadStatusText;
        private set => this.RaiseAndSetIfChanged(ref _squadStatusText, value);
    }

    /// <summary>Only a positively identified solo match hides the squad area.</summary>
    public bool HasSquadSection
    {
        get => _hasSquadSection;
        private set => this.RaiseAndSetIfChanged(ref _hasSquadSection, value);
    }

    public bool HasSquadMembers
    {
        get => _hasSquadMembers;
        private set => this.RaiseAndSetIfChanged(ref _hasSquadMembers, value);
    }

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

    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public bool HasBack => _onBack != null;

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
        _themeService.CycleTheme();
        UpdateThemeDisplay();
    }

    public string? EliminationCoverageNotice
    {
        get => _eliminationCoverageNotice;
        private set
        {
            if (_eliminationCoverageNotice == value) return;
            this.RaiseAndSetIfChanged(ref _eliminationCoverageNotice, value);
            this.RaisePropertyChanged(nameof(HasEliminationCoverageNotice));
        }
    }

    public bool HasEliminationCoverageNotice => !string.IsNullOrEmpty(EliminationCoverageNotice);

    private void UpdateThemeDisplay()
    {
        (ThemeIcon, ThemeToggleTooltip) = _themeService.CurrentTheme switch
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
        ResetReplayState();
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

            ApplySquadProjection(SquadProjection.FromReplay(data));

            ApplyFilter();

            var ownerDisplay = !string.IsNullOrEmpty(data.OwnerName) ? data.OwnerName : "Owner";
            // Event joins can be incomplete, so a missing authoritative owner count must remain unknown.
            var nonNpcEliminations = data.OwnerEliminations.Where(p => !p.IsNpc).ToList();
            var botKills = nonNpcEliminations.Count(p => p.IsBot);
            var observedHumanKills = nonNpcEliminations.Count - botKills;
            if (data.OwnerKills.HasValue)
            {
                var totalKills = data.OwnerKills.Value;
                var incompleteCoverage = data.HasUncertainEliminationAttribution ||
                                         nonNpcEliminations.Count != totalKills;
                if (!incompleteCoverage)
                {
                    OwnerKillsHeader = $"{ownerDisplay}'s Eliminations ({totalKills}) - {observedHumanKills} Players, {botKills} Bots";
                    EliminationCoverageNotice = null;
                }
                else
                {
                    OwnerKillsHeader = $"{ownerDisplay}'s Eliminations ({totalKills}) - {observedHumanKills} Players observed, {botKills} Bots observed";
                    EliminationCoverageNotice = $"Replay records {totalKills} eliminations; {nonNpcEliminations.Count} credited events observed." +
                                                (data.HasUncertainEliminationAttribution ? " Some attribution is uncertain." : "");
                }
                ElimsSummary = incompleteCoverage
                    ? $"{totalKills} Elims ({botKills} Bot{(botKills != 1 ? "s" : "")} observed)"
                    : $"{totalKills} Elims ({botKills} Bot{(botKills != 1 ? "s" : "")})";
            }
            else
            {
                OwnerKillsHeader = string.IsNullOrEmpty(data.OwnerName)
                    ? "Owner analysis incomplete"
                    : $"{ownerDisplay}'s elimination count is unknown";
                ElimsSummary = "Eliminations unknown";
                EliminationCoverageNotice = data.HasUncertainEliminationAttribution
                    ? $"Elimination attribution is uncertain; {nonNpcEliminations.Count} credited events observed."
                    : null;
            }

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
            DurationText = $"{data.Metadata.RecordingDurationMinutes:F1}m";
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
        catch (TimeoutException)
        {
            ErrorMessage = "Replay parsing timed out. This replay may be from a version of Fortnite not yet supported. Please check for an app update.";
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

    private void ApplySquadProjection(SquadProjection projection)
    {
        HasSquadSection = projection.Status != SquadProjectionStatus.ConfirmedSolo;
        _teammates.Clear();
        foreach (var teammate in projection.Teammates)
            _teammates.Add(teammate);
        HasSquadMembers = _teammates.Count > 0;

        SquadStatusText = projection.Status switch
        {
            SquadProjectionStatus.Complete => BuildSquadStatus("Squad confirmed", projection),
            SquadProjectionStatus.Partial => BuildSquadStatus("Squad partially observed", projection),
            SquadProjectionStatus.Unavailable => BuildSquadStatus("Squad data unavailable", projection),
            _ => null
        };
    }

    private static string BuildSquadStatus(string prefix, SquadProjection projection)
    {
        var membership = projection.ObservedTeamSize.HasValue
            ? projection.ExpectedTeamSize.HasValue
                ? $"{projection.ObservedTeamSize} of {projection.ExpectedTeamSize} observed"
                : $"{projection.ObservedTeamSize} observed"
            : projection.ExpectedTeamSize.HasValue
                ? $"expected size {projection.ExpectedTeamSize}"
                : "team size unknown";

        var kills = projection.HasConflictingTeamKills
            ? "team eliminations conflict"
            : projection.TeamKills.HasValue
                ? $"team eliminations {projection.TeamKills}"
                : "team eliminations unknown";
        return $"{prefix}: {membership}; {kills}.";
    }

    private void ResetReplayState()
    {
        _allPlayers.Clear();
        _allOwnerEliminations.Clear();
        _filteredPlayers.Clear();
        _filteredOwnerEliminations.Clear();
        _teammates.Clear();
        OwnerKillsHeader = "Your Eliminations";
        PlayersSeenHeader = "Players Seen";
        WindowTitle = BaseTitle;
        GameMode = null;
        PlaylistName = null;
        PlacementText = null;
        DurationText = null;
        ElimsSummary = null;
        EliminatorName = null;
        EliminationCoverageNotice = null;
        SquadStatusText = null;
        HasSquadSection = false;
        HasSquadMembers = false;
        HasMetadata = false;
        HasData = false;
    }
}
