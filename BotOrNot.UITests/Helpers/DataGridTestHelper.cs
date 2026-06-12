using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using global::Avalonia.Collections;
using global::Avalonia.Controls;
using global::Avalonia.VisualTree;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;

namespace BotOrNot.UITests.Helpers;

public static class DataGridTestHelper
{
    public static Window CreateMainWindowWithData(
        ObservableCollection<PlayerRow> players,
        ObservableCollection<PlayerRow> ownerEliminations)
    {
        var vm = new MainWindowViewModel();

        var allPlayersField = typeof(MainWindowViewModel)
            .GetField("_allPlayers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var allOwnerElimsField = typeof(MainWindowViewModel)
            .GetField("_allOwnerEliminations", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var allPlayers = (List<PlayerRow>)allPlayersField.GetValue(vm)!;
        var allOwnerElims = (List<PlayerRow>)allOwnerElimsField.GetValue(vm)!;

        allPlayers.Clear();
        allOwnerElims.Clear();

        foreach (var p in players) allPlayers.Add(p);
        foreach (var p in ownerEliminations) allOwnerElims.Add(p);

        // Trigger filter to populate the displayed collections.
        vm.FilterText = " ";
        vm.FilterText = "";

        var matchView = new MatchView { DataContext = vm };
        var window = new Window { Content = matchView };
        window.Show();

        return window;
    }

    public static DataGrid GetDataGrid(Window window, string name)
    {
        return window.GetVisualDescendants()
            .OfType<DataGrid>()
            .FirstOrDefault(g => g.Name == name)
            ?? throw new InvalidOperationException($"DataGrid '{name}' not found");
    }

    /// <summary>
    /// Simulates clicking a column header by:
    /// 1. Firing the DataGrid's Sorting event (which sets CustomSortComparer on the column)
    /// 2. Applying the sort via the CollectionView's SortDescriptions
    ///
    /// The comparer is self-contained and returns the final desired order directly,
    /// so it's passed to SortDescriptions as-is (no negation needed).
    /// </summary>
    public static void ClickColumnHeader(Window window, DataGrid dataGrid, string headerText)
    {
        var column = dataGrid.Columns.FirstOrDefault(c => c.Header?.ToString() == headerText)
            ?? throw new InvalidOperationException(
                $"Column '{headerText}' not found. Available: " +
                string.Join(", ", dataGrid.Columns.Select(c => c.Header?.ToString() ?? "(null)")));

        // Step 1: Fire the Sorting event via OnColumnSorting (sets CustomSortComparer)
        var args = new DataGridColumnEventArgs(column);
        var onColumnSorting = typeof(DataGrid).GetMethod(
            "OnColumnSorting",
            BindingFlags.NonPublic | BindingFlags.Instance);
        onColumnSorting!.Invoke(dataGrid, new object[] { args });

        // Step 2: Apply the sort via the CollectionView
        if (column.CustomSortComparer != null)
        {
            var cv = dataGrid.CollectionView;
            if (cv != null)
            {
                var sd = DataGridSortDescription.FromComparer(column.CustomSortComparer);
                cv.SortDescriptions.Clear();
                cv.SortDescriptions.Add(sd);
            }
        }
    }

    /// <summary>
    /// Gets the currently displayed values from the DataGrid's CollectionView,
    /// which reflects the sorted/filtered order.
    /// </summary>
    public static List<string?> GetDisplayedValues(DataGrid dataGrid, Func<PlayerRow, string?> selector)
    {
        var collectionView = dataGrid.CollectionView;
        if (collectionView == null)
            throw new InvalidOperationException("DataGrid CollectionView is null");

        return collectionView.Cast<PlayerRow>().Select(selector).ToList();
    }
}
