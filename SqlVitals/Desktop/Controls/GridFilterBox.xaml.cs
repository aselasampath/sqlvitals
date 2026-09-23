using System.Collections;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SqlVitals.Engine.Filtering;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Filter box for a DataGrid: shows only the rows containing every word typed, in any visible
/// column, as the grid displays it. The filter lives on the grid's view, so it keeps working when
/// the page swaps the rows through <see cref="Helpers.DataGridRefresh.SetItemsSource"/>.
/// </summary>
public partial class GridFilterBox : UserControl
{
    public static readonly DependencyProperty TargetProperty =
        DependencyProperty.Register(nameof(Target), typeof(DataGrid), typeof(GridFilterBox),
            new PropertyMetadata(null, OnTargetChanged));

    public DataGrid? Target
    {
        get => (DataGrid?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Raised after the filter text has been applied to the grid.</summary>
    public event EventHandler? FilterChanged;

    public bool IsActive => _terms.Count > 0;

    // Typing into a grid of a few thousand rows re-filters on every pause, not every key.
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Each row's searchable text is built once and kept for as long as the row object lives,
    // so a refresh (new row objects) naturally starts fresh.
    private readonly ConditionalWeakTable<object, string> _rowText = new();

    private readonly Predicate<object> _predicate;
    private IReadOnlyList<string> _terms = [];

    public GridFilterBox()
    {
        InitializeComponent();
        _predicate = Matches;
        _debounce.Tick += (_, _) => Apply();
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (GridFilterBox)d;
        if (e.OldValue is DataGrid oldGrid)
            ((INotifyCollectionChanged)oldGrid.Items).CollectionChanged -= box.Items_CollectionChanged;
        if (e.NewValue is DataGrid newGrid)
            ((INotifyCollectionChanged)newGrid.Items).CollectionChanged += box.Items_CollectionChanged;
        box.Apply();
    }

    // Keeps "N of M" right when the page reloads the grid underneath the filter.
    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateCount();

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = TxtFilter.Text.Length == 0;
        TxtPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BtnClear.Visibility       = empty ? Visibility.Collapsed : Visibility.Visible;

        _debounce.Stop();
        _debounce.Start();
    }

    private void TxtFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TxtFilter.Text.Length > 0)
        {
            TxtFilter.Clear();
            Apply();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Apply();
            e.Handled = true;
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        TxtFilter.Clear();
        Apply();
        TxtFilter.Focus();
    }

    private void Apply()
    {
        _debounce.Stop();
        _terms = RowFilter.ParseTerms(TxtFilter.Text);

        var grid = Target;
        if (grid is null || !grid.Items.CanFilter) return;

        if (_terms.Count == 0)
        {
            if (grid.Items.Filter is not null) grid.Items.Filter = null;
        }
        else if (ReferenceEquals(grid.Items.Filter, _predicate))
        {
            // Same predicate, new terms: re-run it. ItemCollection.Refresh() does not reach the
            // underlying view when the grid is bound to an ItemsSource, so refresh that view.
            CollectionViewSource.GetDefaultView(grid.ItemsSource)?.Refresh();
        }
        else
        {
            grid.Items.Filter = _predicate;
        }

        UpdateCount();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCount()
    {
        var grid = Target;
        if (grid is null || !IsActive)
        {
            TxtCount.Text = "";
            return;
        }

        int total = grid.Items.SourceCollection switch
        {
            null          => 0,
            ICollection c => c.Count,
            var source    => source.Cast<object>().Count(),
        };
        TxtCount.Text = $"{grid.Items.Count:N0} of {total:N0} rows";
    }

    private bool Matches(object item)
    {
        if (_terms.Count == 0) return true;

        if (!_rowText.TryGetValue(item, out var text))
        {
            text = BuildRowText(item);
            _rowText.AddOrUpdate(item, text);
        }
        return RowFilter.Matches(text, _terms);
    }

    // The visible data columns, formatted the way the cells show them. Columns with no clipboard
    // binding, such as the "View Plan" buttons, hold no text and are skipped.
    private string BuildRowText(object item)
    {
        var grid = Target;
        if (grid is null) return "";

        var culture = grid.Language.GetSpecificCulture();
        return RowFilter.RowText(grid.Columns
            .Where(c => c.Visibility == Visibility.Visible && c.ClipboardContentBinding is not null)
            .Select(c => RowFilter.CellText(
                c.OnCopyingCellClipboardContent(item),
                (c as DataGridBoundColumn)?.Binding is Binding b ? b.StringFormat : null,
                culture)));
    }
}
