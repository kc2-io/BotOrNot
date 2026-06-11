using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;
using ReactiveUI;

namespace BotOrNot.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly MenuFlyout _columnsFlyout;
    // All columns use a 3-mode cycle:
    //   0 = desc (unknowns bottom), 1 = asc (unknowns bottom), 2 = unknowns-first
    private readonly Dictionary<DataGridColumn, int> _columnSortMode = new();
    private DataGrid? _playersGrid;
    private Button? _columnsButton;
    private IStorageFolder? _lastReplayFolder;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        _columnsFlyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        // Get references to controls and set up the flyout
        _playersGrid = this.FindControl<DataGrid>("PlayersGrid");
        _columnsButton = this.FindControl<Button>("ColumnsButton");

        if (_columnsButton != null)
        {
            _columnsButton.Flyout = _columnsFlyout;
        }

        // Wire up custom sorting and row tinting on both grids
        var ownerGrid = this.FindControl<DataGrid>("OwnerEliminationsGrid");
        if (ownerGrid != null)
        {
            ownerGrid.Sorting += OnDataGridSorting;
            ownerGrid.LoadingRow += OnDataGridLoadingRow;
        }
        if (_playersGrid != null)
        {
            _playersGrid.Sorting += OnDataGridSorting;
            _playersGrid.LoadingRow += OnDataGridLoadingRow;
        }

        // Drag-and-drop handlers
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnFileDrop);

        // Build the columns menu when the window loads
        Loaded += (_, _) => BuildColumnsFlyout();

        // Refresh DataGrid row backgrounds when theme changes
        if (global::Avalonia.Application.Current != null)
        {
            global::Avalonia.Application.Current.ActualThemeVariantChanged += (_, _) =>
            {
                RefreshDataGridRows(ownerGrid);
                RefreshDataGridRows(_playersGrid);
            };
        }
    }

    private static void RefreshDataGridRows(DataGrid? grid)
    {
        if (grid?.ItemsSource == null) return;
        var source = grid.ItemsSource;
        grid.ItemsSource = null;
        grid.ItemsSource = source;
    }

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        var info = GetColumnSortInfo(e.Column);
        if (info.Selector == null) return;

        var items = grid.ItemsSource as IEnumerable<PlayerRow>;

        IComparer comparer;

        if (info.GroupCycle)
        {
            // Group-cycle columns: each click brings the next distinct value to the top
            comparer = (IComparer)BuildGroupCycleComparer(e.Column, info, items);
        }
        else
        {
            comparer = (IComparer)BuildStandardComparer(e.Column, info, items);
        }

        // Set on column so DataGrid knows sorting is active
        e.Column.CustomSortComparer = comparer;

        // The DataGrid's internal sort may apply the wrong direction (it toggles
        // its own asc/desc cycle independently of our mode cycle). Post a
        // callback to re-apply the correct sort after DataGrid finishes.
        Dispatcher.UIThread.Post(() =>
        {
            var cv = grid.CollectionView;
            if (cv != null)
            {
                cv.SortDescriptions.Clear();
                cv.SortDescriptions.Add(DataGridSortDescription.FromComparer(comparer));
            }
        });
    }

    private void OnDataGridLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is PlayerRow player)
        {
            var app = global::Avalonia.Application.Current;
            var botBrush = app?.FindResource("BotRowBackground") as IBrush;
            var defaultBrush = app?.FindResource("DefaultRowBackground") as IBrush;
            e.Row.Background = player.IsBot ? botBrush : defaultBrush;
        }
    }

    private IComparer<PlayerRow> BuildStandardComparer(
        DataGridColumn column, ColumnSortInfo info, IEnumerable<PlayerRow>? items)
    {
        // Determine cycle length: text columns always 2-mode (asc/desc).
        // Numeric/bot columns get 3-mode only when the data has unknowns.
        var hasUnknowns = false;
        if (info.Numeric || info.Bot)
        {
            if (items != null)
                hasUnknowns = items.Any(p => PlayerRowSortComparer.IsUnknownOrEmpty(info.Selector!(p)));
        }
        var modeCount = (info.Numeric || info.Bot) && hasUnknowns ? 3 : 2;

        _columnSortMode.TryGetValue(column, out var currentMode);
        var nextMode = (currentMode + 1) % modeCount;
        _columnSortMode[column] = nextMode;

        // Modes (nextMode starts at 1 because currentMode defaults to 0):
        // 2-mode: 1=asc, 0=desc
        // 3-mode: 1=asc, 2=unknowns-first, 0=desc
        var descending = nextMode == 0;
        var unknownsFirst = modeCount == 3 && nextMode == 2;

        return new PlayerRowSortComparer(
            info.Selector!, descending: descending, numeric: info.Numeric,
            isBotField: info.Bot, unknownsFirst: unknownsFirst);
    }

    private IComparer<PlayerRow> BuildGroupCycleComparer(
        DataGridColumn column, ColumnSortInfo info, IEnumerable<PlayerRow>? items)
    {
        // Get distinct non-empty values sorted alphabetically
        var groups = (items ?? [])
            .Select(p => info.Selector!(p))
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var modeCount = Math.Max(groups.Count, 1);

        _columnSortMode.TryGetValue(column, out var currentMode);
        var nextMode = (currentMode + 1) % modeCount;
        _columnSortMode[column] = nextMode;

        var targetGroup = groups.Count > 0 ? groups[nextMode] : null;
        return new GroupCycleComparer(info.Selector!, targetGroup);
    }

    private record struct ColumnSortInfo(
        Func<PlayerRow, string?>? Selector, bool Numeric, bool Bot, bool GroupCycle);

    private static ColumnSortInfo GetColumnSortInfo(DataGridColumn column)
    {
        return column.Header?.ToString() switch
        {
            "Id" => new(p => p.Id, false, false, false),
            "Name" => new(p => p.Name, false, false, false),
            "Level" => new(p => p.Level, true, false, false),
            "Bot" => new(p => p.Bot, false, true, false),
            "Platform" => new(p => p.Platform, false, false, true),
            "Kills" => new(p => p.Kills, true, false, false),
            "Squad" => new(p => p.TeamIndex, true, false, false),
            "Place" => new(p => p.Placement, true, false, false),
            "Death Cause" => new(p => p.DeathCause, false, false, true),
            "Elim Time" => new(p => p.ElimTime, true, false, false),
            "Pickaxe" => new(p => p.Pickaxe, false, false, false),
            "Glider" => new(p => p.Glider, false, false, false),
            _ => default
        };
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files != null && files.Any(f => f.Name.EndsWith(".replay", StringComparison.OrdinalIgnoreCase)))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        _viewModel.IsDropTargetActive = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _viewModel.IsDropTargetActive = false;
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        _viewModel.IsDropTargetActive = false;

        try
        {
            var files = e.Data.GetFiles();
            var replayFile = files?.FirstOrDefault(f => f.Name.EndsWith(".replay", StringComparison.OrdinalIgnoreCase));
            var path = replayFile?.TryGetLocalPath();

            if (!string.IsNullOrEmpty(path))
            {
                await _viewModel.LoadReplayCommand.Execute(path).FirstAsync();
            }
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"Failed to load replay: {ex.Message} (The file may still be locked by Fortnite.)";
            _viewModel.IsLoading = false;
        }
    }

    private async void OpenReplay_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Fortnite Replay File",
                AllowMultiple = false,
                SuggestedStartLocation = _lastReplayFolder,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Fortnite Replays")
                    {
                        Patterns = new[] { "*.replay" }
                    },
                    new FilePickerFileType("All Files")
                    {
                        Patterns = new[] { "*.*" }
                    }
                }
            });

            if (files.Count > 0)
            {
                var file = files[0];
                _lastReplayFolder = await file.GetParentAsync();
                var path = file.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                {
                    await _viewModel.LoadReplayCommand.Execute(path).FirstAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"Failed to load replay: {ex.Message} (The file may still be locked by Fortnite.)";
            _viewModel.IsLoading = false;
        }
    }

    private void OpenFortniteTracker_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlayerRow player } && !string.IsNullOrEmpty(player.Name))
        {
            var encodedName = Uri.EscapeDataString(player.Name);
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://fortnitetracker.com/profile/all/{encodedName}",
                UseShellExecute = true
            });
        }
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (_playersGrid == null) return;

        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder for CSV export",
            AllowMultiple = false
        });

        if (folder.Count == 0) return;

        var dirPath = folder[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(dirPath)) return;

        var columns = GetVisibleCsvColumns();
        if (columns.Count == 0) return;

        var baseName = _viewModel.WindowTitle.Contains(" - ")
            ? _viewModel.WindowTitle.Split(" - ", 2)[1]
            : "replay_export";

        var ownerCsv = CsvExportService.GenerateCsv(_viewModel.OwnerEliminations, columns);
        var playersCsv = CsvExportService.GenerateCsv(_viewModel.Players, columns);

        var elimPath = Path.Combine(dirPath, $"{baseName}_eliminations.csv");
        var playersPath = Path.Combine(dirPath, $"{baseName}_players.csv");

        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(elimPath, ownerCsv, utf8Bom);
        await File.WriteAllTextAsync(playersPath, playersCsv, utf8Bom);
    }

    private List<CsvColumnDefinition> GetVisibleCsvColumns()
    {
        if (_playersGrid == null) return new();

        return _playersGrid.Columns
            .Where(c => c.IsVisible)
            .Select(c => MapColumnToCsv(c.Header?.ToString()))
            .Where(c => c != null)
            .ToList()!;
    }

    private static CsvColumnDefinition? MapColumnToCsv(string? header)
    {
        return header switch
        {
            "Id" => new(header, p => p.Id),
            "Name" => new(header, p => p.Name ?? ""),
            "Level" => new(header, p => UnknownToDash(p.Level)),
            "Bot" => new(header, p => BotToText(p.Bot)),
            "Platform" => new(header, p => PlatformToText(p.Platform)),
            "Kills" => new(header, p => UnknownToDash(p.Kills)),
            "Squad" => new(header, p => SquadToText(p.TeamIndex)),
            "Place" => new(header, p => UnknownToDash(p.Placement)),
            "Death Cause" => new(header, p => p.DeathCause ?? ""),
            "Elim Time" => new(header, p => p.ElimTime ?? ""),
            "Pickaxe" => new(header, p => p.Pickaxe ?? ""),
            "Glider" => new(header, p => p.Glider ?? ""),
            _ => null
        };
    }

    private static string UnknownToDash(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return "-";
        return value;
    }

    private static string BotToText(string? bot)
    {
        return bot?.ToLowerInvariant() switch
        {
            "true" => "Yes",
            "false" => "No",
            _ => "Unknown"
        };
    }

    private static string PlatformToText(string? platform)
    {
        var friendly = PlatformHelper.GetFriendlyName(platform);
        return friendly == "Unknown" ? "-" : friendly;
    }

    private static string SquadToText(string? teamIndex)
    {
        if (!string.IsNullOrWhiteSpace(teamIndex) && !teamIndex.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return $"Squad # {teamIndex}";
        return "-";
    }

    private void BuildColumnsFlyout()
    {
        if (_playersGrid == null) return;

        _columnsFlyout.Items.Clear();

        foreach (var column in _playersGrid.Columns)
        {
            var menuItem = new MenuItem
            {
                Header = column.Header?.ToString() ?? "Column",
                Icon = column.IsVisible ? new CheckBox { IsChecked = true, IsHitTestVisible = false } : null,
                Tag = column
            };

            menuItem.Click += (sender, _) =>
            {
                if (sender is MenuItem item && item.Tag is DataGridColumn col)
                {
                    // Don't hide the last visible column
                    var visibleCount = _playersGrid.Columns.Count(c => c.IsVisible);
                    if (col.IsVisible && visibleCount <= 1)
                    {
                        return;
                    }

                    col.IsVisible = !col.IsVisible;
                    item.Icon = col.IsVisible ? new CheckBox { IsChecked = true, IsHitTestVisible = false } : null;
                }
            };

            _columnsFlyout.Items.Add(menuItem);
        }

        if (_playersGrid.Columns.Count > 0)
        {
            _columnsFlyout.Items.Add(new Separator());

            var showAllItem = new MenuItem { Header = "Show All" };
            showAllItem.Click += (_, _) =>
            {
                foreach (var col in _playersGrid.Columns)
                {
                    col.IsVisible = true;
                }
                BuildColumnsFlyout();
            };
            _columnsFlyout.Items.Add(showAllItem);
        }
    }
}
