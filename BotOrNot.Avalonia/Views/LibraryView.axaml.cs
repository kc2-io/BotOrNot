using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Core.Models;

namespace BotOrNot.Avalonia.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private async void PickDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as LibraryViewModel;
        if (vm == null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Fortnite Replays Folder",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await vm.SetDirectoryCommand.Execute(path).FirstAsync();
        await vm.ScanCommand.Execute().FirstAsync();
    }

    private void ReplayGrid_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as LibraryViewModel;
        if (vm == null) return;

        var grid = sender as DataGrid;
        if (grid?.SelectedItem is ReplaySummary summary)
            vm.OpenReplayCommand.Execute(summary).Subscribe();
    }

    private void ReplayScanLimit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LibraryViewModel viewModel)
            return;

        e.Handled = true;
        viewModel.ApplyScanLimitCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private void AutoRefreshMinutes_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LibraryViewModel viewModel)
            return;

        e.Handled = true;
        viewModel.ApplyAutoRefreshMinutesCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private void OpponentChip_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel &&
            sender is ToggleButton { DataContext: FrequentOpponent opponent })
            viewModel.ToggleOpponentFilter(opponent);
    }
}
