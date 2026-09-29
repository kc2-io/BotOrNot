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

    private readonly ObservableCollection<SquadMemberSummary> _teammates = new();
    private string _ownerKillsHeader = "Your Eliminations";
    private string _playersSeenHeader = "Players Seen";
    private string _npcsSeenHeader = "NPCs Seen";
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
    private bool _hasNpcs;
    private bool _isDropTargetActive;
    private string _themeIcon = "\u2699";
    private string _themeToggleTooltip = "Theme: System";

    private readonly List<PlayerRow> _allPlayers = new();
    private readonly List<PlayerRow> _allOwnerEliminations = new();
    private readonly List<PlayerRow> _allNpcs = new();

    private readonly ObservableCollection<PlayerRow> _filteredPlayers = new();
    private readonly ObservableCollection<PlayerRow> _filteredOwnerEliminations = new();
    private readonly ObservableCollection<PlayerRow> _filteredNpcs = new();

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

    public ObservableCollection<PlayerRow> Npcs => _filteredNpcs;

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

    public string NpcsSeenHeader
    {
        get => _npcsSeenHeader;
        set => this.RaiseAndSetIfChanged(ref _npcsSeenHeader, value);
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

    public bool HasNpcs
    {
        get => _hasNpcs;
        private set => this.RaiseAndSetIfChanged(ref _hasNpcs, value);
    }

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
        var searchTerm = FilterText;
        ReplaceContents(_filteredPlayers, _allPlayers.Where(p => MatchesFilter(p, searchTerm)));
        ReplaceContents(_filteredOwnerEliminations, _allOwnerEliminations.Where(p => MatchesFilter(p, searchTerm)));
        ReplaceContents(_filteredNpcs, _allNpcs.Where(p => MatchesNpcFilter(p, searchTerm)));
    }

    private static void ReplaceContents(ObservableCollection<PlayerRow> target, IEnumerable<PlayerRow> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private static IEnumerable<PlayerRow> OrderByElimTime(IEnumerable<PlayerRow> rows)
    {
        return rows.OrderBy(p => string.IsNullOrEmpty(p.ElimTime) ? 1 : 0)
                   .ThenBy(p => p.ElimTime, StringComparer.Ordinal);
    }

    internal static bool MatchesFilter(PlayerRow player, string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return true;

        return (player.Name?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.Level?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               PlatformHelper.GetFriendlyName(player.Platform).Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
               (player.Kills?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.TeamIndex?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.Placement?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.DeathCause?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.ElimTime?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    internal static bool MatchesNpcFilter(PlayerRow player, string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return true;

        return (player.Name?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (player.DeathCause?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task LoadReplayAsync(string path)
    {
        ResetReplayState();
        ErrorMessage = null;
        IsLoading = true;

        try
        {
            var data = await _replayService.LoadReplayAsync(path);

            var partitionedPlayers = data.Players.ToLookup(p => p.IsNpc);
            var allPlayers = OrderByElimTime(partitionedPlayers[false]).ToList();
            var allNpcs = partitionedPlayers[true].OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var allOwnerEliminations = OrderByElimTime(data.OwnerEliminations.Where(p => !p.IsNpc)).ToList();

            var ownerKilledNpcIds = new HashSet<string>(
                data.OwnerEliminations.Where(p => p.IsNpc).Select(p => p.Id),
                StringComparer.OrdinalIgnoreCase);
            foreach (var npc in allNpcs)
                npc.KilledByOwner = ownerKilledNpcIds.Contains(npc.Id);

            var ownerDisplay = !string.IsNullOrEmpty(data.OwnerName) ? data.OwnerName : "Owner";
            // Event joins can be incomplete, so a missing authoritative owner count must remain unknown.
            var botKills = allOwnerEliminations.Count(p => p.IsBot);
            var observedHumanKills = allOwnerEliminations.Count - botKills;
            if (data.OwnerKills.HasValue)
            {
                var totalKills = data.OwnerKills.Value;
                var incompleteCoverage = data.HasUncertainEliminationAttribution ||
                                         allOwnerEliminations.Count != totalKills;
                if (!incompleteCoverage)
                {
                    OwnerKillsHeader = $"{ownerDisplay}'s Eliminations ({totalKills}) - {observedHumanKills} Players, {botKills} Bots";
                    EliminationCoverageNotice = null;
                }
                else
                {
                    OwnerKillsHeader = $"{ownerDisplay}'s Eliminations ({totalKills}) - {observedHumanKills} Players observed, {botKills} Bots observed";
                    EliminationCoverageNotice = $"Replay records {totalKills} eliminations; {allOwnerEliminations.Count} credited events observed." +
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
                    ? $"Elimination attribution is uncertain; {allOwnerEliminations.Count} credited events observed."
                    : null;
            }

            var totalPlayers = allPlayers.Count;
            var botPlayers = allPlayers.Count(p => p.IsBot);
            var humanPlayers = totalPlayers - botPlayers;
            var platformGroups = allPlayers
                .Where(p => !string.IsNullOrWhiteSpace(p.Platform))
                .GroupBy(p => PlatformHelper.GetFriendlyName(p.Platform))
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();
            var platformBreakdown = platformGroups.Count > 0 ? " | " + string.Join(", ", platformGroups) : "";
            var playersSeenHeader = $"Players Seen ({totalPlayers}) - {humanPlayers} Players, {botPlayers} Bots{platformBreakdown}";
            var npcsSeenHeader = $"NPCs Seen ({allNpcs.Count})";

            var ownerPlayer = data.Players.FirstOrDefault(p =>
                !string.IsNullOrEmpty(data.OwnerName) &&
                p.Name?.Equals(data.OwnerName, StringComparison.OrdinalIgnoreCase) == true);
            var ownerPlacement = ownerPlayer?.Placement;
            var eliminatorName = ownerPlacement is not null and not "1" && data.OwnerEliminatedBy != null
                ? data.OwnerEliminatedBy
                : null;

            // Commit all state atomically now that everything has been computed successfully.
            _allPlayers.Clear();
            _allPlayers.AddRange(allPlayers);
            _allNpcs.Clear();
            _allNpcs.AddRange(allNpcs);
            _allOwnerEliminations.Clear();
            _allOwnerEliminations.AddRange(allOwnerEliminations);
            ApplySquadProjection(SquadProjection.FromReplay(data));
            ApplyFilter();

            PlayersSeenHeader = playersSeenHeader;
            NpcsSeenHeader = npcsSeenHeader;
            EliminatorName = eliminatorName;
            WindowTitle = $"{BaseTitle} - {data.Metadata.FileName}";
            GameMode = data.Metadata.GameMode;
            PlaylistName = data.Metadata.Playlist;
            PlacementText = !string.IsNullOrEmpty(ownerPlacement) ? $"#{ownerPlacement}" : "?";
            DurationText = $"{data.Metadata.RecordingDurationMinutes:F1}m";
            HasMetadata = true;
            HasData = true;
            HasNpcs = allNpcs.Count > 0;
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
        _allNpcs.Clear();
        _filteredPlayers.Clear();
        _filteredOwnerEliminations.Clear();
        _filteredNpcs.Clear();
        _teammates.Clear();
        OwnerKillsHeader = "Your Eliminations";
        PlayersSeenHeader = "Players Seen";
        NpcsSeenHeader = "NPCs Seen";
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
        HasNpcs = false;
    }
}
