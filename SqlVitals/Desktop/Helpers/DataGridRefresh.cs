using System.Collections;
using System.Windows.Controls;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// Replaces a grid's rows without losing how the user is looking at them. Assigning a new
/// ItemsSource creates a new collection view, and DataGrid clears the column sort when that
/// happens, so a refresh would otherwise reshuffle the grid and drop any filter.
/// </summary>
public static class DataGridRefresh
{
    public static void SetItemsSource(DataGrid grid, IEnumerable items)
    {
        var sorts  = grid.Items.SortDescriptions.ToList();
        var filter = grid.Items.Filter;

        grid.ItemsSource = items;

        if (filter is not null && grid.Items.CanFilter)
            grid.Items.Filter = filter;

        grid.Items.SortDescriptions.Clear();
        foreach (var sort in sorts)
            grid.Items.SortDescriptions.Add(sort);

        // The header arrows live on the columns, not the view.
        foreach (var column in grid.Columns)
        {
            var match = sorts.FirstOrDefault(s => s.PropertyName == column.SortMemberPath);
            column.SortDirection = match.PropertyName is null ? null : match.Direction;
        }
    }
}
