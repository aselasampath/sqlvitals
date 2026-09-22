using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Export;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// Attached behavior that gives a DataGrid a right-click menu with Copy, Copy with headers
/// and Export to CSV. It is switched on for every grid by the implicit DataGrid style in the
/// theme dictionaries, so pages do not wire it up themselves.
/// </summary>
public static class DataGridExport
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(DataGridExport),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    // Marks menus this behavior created, so a grid's own ContextMenu is never replaced or removed.
    private const string MenuTag = nameof(DataGridExport);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;

        if ((bool)e.NewValue)
        {
            if (grid.ContextMenu is null) grid.ContextMenu = BuildMenu(grid);
        }
        else if (grid.ContextMenu is { Tag: MenuTag })
        {
            grid.ClearValue(FrameworkElement.ContextMenuProperty);
        }
    }

    private static ContextMenu BuildMenu(DataGrid grid)
    {
        var copy        = new MenuItem { Header = "_Copy" };
        var copyHeaders = new MenuItem { Header = "Copy with _headers" };
        var export      = new MenuItem { Header = "_Export to CSV…" };

        copy.Click        += (_, _) => Copy(grid, includeHeaders: false);
        copyHeaders.Click += (_, _) => Copy(grid, includeHeaders: true);
        export.Click      += (_, _) => ExportCsv(grid);

        var menu = new ContextMenu { Tag = MenuTag, Items = { copy, copyHeaders, new Separator(), export } };
        menu.Opened += (_, _) =>
        {
            bool hasRows = DataItems(grid).Any();
            copy.IsEnabled = copyHeaders.IsEnabled = export.IsEnabled = hasRows;
        };
        return menu;
    }

    // Copies the selected rows, or every row when nothing is selected, as tab-separated text.
    private static void Copy(DataGrid grid, bool includeHeaders)
    {
        var all      = DataItems(grid).ToList();
        var selected = grid.SelectedItems.Cast<object>().ToHashSet();
        var items    = selected.Count > 0 ? all.Where(selected.Contains).ToList() : all;

        var columns = ExportColumns(grid);
        ClipboardHelper.SetText(DelimitedText.ToTsv(
            includeHeaders ? Headers(columns) : null, Rows(items, columns)));
    }

    // Exports every row in the grid's current sort order, whatever is selected.
    private static void ExportCsv(DataGrid grid)
    {
        var dialog = new SaveFileDialog
        {
            Title      = "Export to CSV",
            Filter     = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName   = $"SqlVitals-{(string.IsNullOrEmpty(grid.Name) ? "Grid" : grid.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(grid)) != true) return;

        try
        {
            var columns = ExportColumns(grid);
            var csv = DelimitedText.ToCsv(Headers(columns), Rows(DataItems(grid).ToList(), columns));
            File.WriteAllText(dialog.FileName, csv, DelimitedText.CsvEncoding);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Export to CSV failed: {dialog.FileName}", ex);
            MessageBox.Show($"Could not export to CSV:\n{ex.Message}",
                "Export Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static IEnumerable<object> DataItems(DataGrid grid) =>
        grid.Items.Cast<object>().Where(i => i != CollectionView.NewItemPlaceholder);

    // Visible columns in display order. Columns with no clipboard binding, such as the
    // "View Plan" button columns, hold no data and are left out.
    private static List<DataGridColumn> ExportColumns(DataGrid grid) =>
        grid.Columns
            .Where(c => c.Visibility == Visibility.Visible && c.ClipboardContentBinding is not null)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

    private static List<string> Headers(List<DataGridColumn> columns) =>
        columns.Select(c => c.Header switch
        {
            null          => "",
            string s      => s,
            TextBlock tb  => tb.Text,
            ContentControl { Content: var content } => content?.ToString() ?? "",
            var other     => other.ToString() ?? "",
        }).ToList();

    private static IEnumerable<IReadOnlyList<object?>> Rows(List<object> items, List<DataGridColumn> columns) =>
        items.Select(item => (IReadOnlyList<object?>)columns.Select(c => c.OnCopyingCellClipboardContent(item)).ToList());
}
