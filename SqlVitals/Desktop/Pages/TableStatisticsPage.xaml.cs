using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Table Statistics, bound to <see cref="TableStatisticsViewModel"/> (#242). The code-behind applies
/// the view model's filters to the grids' views, says how many rows show, and keeps the table grid's
/// selection and the view model's in step: only a row chosen changes the table, so a filter that
/// hides it, which clears the grid's selection, leaves its details as they are.
/// </summary>
public partial class TableStatisticsPage : Page, IPageContext
{
    // Read once, and again on Refresh.
    public PageKind Kind => PageKind.Snapshot;

    // False: the bar would name the connection's database, and this page shows the one it reads itself.
    public bool UsesDatabase => false;

    internal TableStatisticsViewModel ViewModel { get; }

    // While the grid is made to match the view model, its changes aren't the user's.
    private bool _matching;

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
        ViewModel.PropertyChanged     += ViewModel_PropertyChanged;

        TablesFilter.FilterChanged += (_, _) => TablesChanged();
        ((INotifyCollectionChanged)TablesGrid.Items).CollectionChanged += (_, _) => TablesChanged();
        IndexesFilter.FilterChanged += (_, _) => ViewModel.IndexesShown(IndexesGrid.Items.Count);
        ((INotifyCollectionChanged)IndexesGrid.Items).CollectionChanged += (_, _) => ViewModel.IndexesShown(IndexesGrid.Items.Count);

        ApplyTableFilters();
        ApplyIndexFilters();
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
