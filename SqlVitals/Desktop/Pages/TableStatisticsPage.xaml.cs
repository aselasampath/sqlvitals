using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Table Statistics, bound to <see cref="TableStatisticsViewModel"/> (#242). The code-behind applies
/// the view model's filters to the grids' views, says how many rows show, and keeps the table grid's
/// selection and the view model's in step: only a row chosen changes the table, so a filter that
/// hides it, which clears the grid's selection, leaves its details as they are. On the Statistics tab
/// (#243), which holds more than fits, the tables take a fixed height and the page scrolls.
/// </summary>
public partial class TableStatisticsPage : Page, IPageContext, IFocusPage
{
    // A link opens it on a table (#244): every table stays listed, so there's nothing to show all of.
    public void ShowFocus(PageFocus focus) => ViewModel.OpenOn(focus.Filter.Table);

    public void ClearFocus() => ViewModel.OpenOn(null);

    public string? FocusNote => null;

    public string? ShowAllText => null;

    // Read once, and again on Refresh.
    public PageKind Kind => PageKind.Snapshot;

    // False: the bar would name the connection's database, and this page shows the one it reads itself.
    public bool UsesDatabase => false;

    internal TableStatisticsViewModel ViewModel { get; }

    // While the grid is made to match the view model, its changes aren't the user's.
    private bool _matching;

    /// <summary>The tables' height with the Statistics tab showing.</summary>
    internal const double TablesHeightOnStatistics = 280;

    // The rows' heights on Sizes, which the splitter may have changed.
    private GridLength _tablesHeight;
    private GridLength _detailsHeight;
    private bool _statisticsLayout;

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public TableStatisticsPage(TableStatisticsViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;

        ViewModel.TableFiltersChanged += ApplyTableFilters;
        ViewModel.IndexFiltersChanged += ApplyIndexFilters;
        ViewModel.TableSearchCleared  += TablesFilter.Clear;
        ViewModel.IndexSearchCleared  += IndexesFilter.Clear;
        ViewModel.ShowRow             += ShowTable;
        ViewModel.TableHealthArrived  += ShowHealth;
        ViewModel.PropertyChanged     += ViewModel_PropertyChanged;

        TablesFilter.FilterChanged += (_, _) => TablesChanged();
        ((INotifyCollectionChanged)TablesGrid.Items).CollectionChanged += (_, _) => TablesChanged();
        IndexesFilter.FilterChanged += (_, _) => ViewModel.IndexesShown(IndexesGrid.Items.Count);
        ((INotifyCollectionChanged)IndexesGrid.Items).CollectionChanged += (_, _) => ViewModel.IndexesShown(IndexesGrid.Items.Count);

        ApplyTableFilters();
        ApplyIndexFilters();
        NestedScrolling.Attach(PageScroller);
        PageScroller.ScrollChanged += PageScroller_ScrollChanged;
        ApplyDetailLayout();
    }

    // The page's scrollbar came or went: a star column doesn't give up the width it costs by itself, and
    // the grid would scroll sideways by that much.
    private void PageScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PageScroller) || e.ViewportWidthChange == 0)
            return;
        foreach (var column in TablesGrid.Columns.Where(c => c.Width.IsStar))
        {
            var width = column.Width;
            column.Width = new DataGridLength(column.MinWidth);
            column.Width = width;
        }
    }

    // The sort and the words typed have only what the rows said before.
    private void ShowHealth()
    {
        TablesFilter.RowTextChanged();
        if (TablesGrid.Items.SortDescriptions.Any(s => s.PropertyName is nameof(TableSizeRow.HealthOrder) or nameof(TableSizeRow.MostChangedPct) or nameof(TableSizeRow.OldestUpdate)))
            CollectionViewSource.GetDefaultView(TablesGrid.ItemsSource)?.Refresh();
        TablesChanged();
    }

    // Statistics: the tables at a fixed height, and the tab as tall as it needs, for the page to scroll
    // (PageShell.KeepsDetailMinHeight). The splitter has nothing to share, so it goes while it shows.
    private void ApplyDetailLayout()
    {
        if (ViewModel.ShowsStatistics == _statisticsLayout)
            return;
        _statisticsLayout = ViewModel.ShowsStatistics;
        if (_statisticsLayout)
        {
            _tablesHeight = TablesRow.Height;
            _detailsHeight = DetailsRow.Height;
            TablesRow.Height = new GridLength(TablesHeightOnStatistics);
            DetailsRow.Height = GridLength.Auto;
        }
        else
        {
            TablesRow.Height = _tablesHeight;
            DetailsRow.Height = _detailsHeight;
            PageScroller.ScrollToTop();
        }
    }

    private void ApplyTableFilters()
    {
        TablesFilter.Refilter(ViewModel.HasTableFilters ? ViewModel.ShowsTable : null);
        MatchSelection();
    }

    private void ApplyIndexFilters() => IndexesFilter.Refilter(ViewModel.HasIndexFilters ? ViewModel.ShowsIndex : null);

    private void TablesChanged()
    {
        ViewModel.TablesShown(TablesGrid.Items.Count);
        MatchSelection();
    }

    private void TablesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_matching && TablesGrid.SelectedItem is TableSizeRow row)
            ViewModel.SelectedTable = row;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableStatisticsViewModel.SelectedTable))
            MatchSelection();
        else if (e.PropertyName == nameof(TableStatisticsViewModel.ShowsStatistics))
            ApplyDetailLayout();
    }

    // The grid selects the view model's table whenever a filter lets it show.
    private void MatchSelection()
    {
        var wanted = ViewModel.SelectedTable;
        if (ReferenceEquals(TablesGrid.SelectedItem, wanted) || (wanted is not null && !TablesGrid.Items.Contains(wanted)))
            return;
        _matching = true;
        try
        {
            TablesGrid.SelectedItem = wanted;
        }
        finally
        {
            _matching = false;
        }
    }

    // The verdict's Show it: past a filter that hides it, and in view.
    private void ShowTable(TableSizeRow row)
    {
        if (!TablesGrid.Items.Contains(row))
            ViewModel.ClearTableFiltersCommand.Execute(null);
        MatchSelection();
        TablesGrid.ScrollIntoView(row);
    }
}
