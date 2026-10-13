using System.ComponentModel;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Stale Statistics, bound to <see cref="StaleStatisticsViewModel"/> (#184): the code-behind keeps
/// the grid's rows selected and the view model's in step, which a grid can't bind, and scrolls the
/// one the verdict shows into view.
/// </summary>
public partial class StaleStatisticsPage : Page, IPageContext
{
    // Read once, and again on Refresh, from the connection's database (#140).
    public PageKind Kind => PageKind.Snapshot;
    public bool UsesDatabase => true;

    internal StaleStatisticsViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the explanation's folds are remembered; none in some tests.</param>
    public StaleStatisticsPage(StaleStatisticsViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ViewModel.ShowRow += StatsGrid.ScrollIntoView;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    // While the grid is made to match the view model, its changes aren't the user's.
    private bool _matching;

    private void StatsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_matching)
            ViewModel.SelectedStatistics = StatsGrid.SelectedItems.OfType<StaleStatistic>().ToList();
    }

    // The view model kept the rows selected across a refresh: the grid selects them all again.
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StaleStatisticsViewModel.SelectedStatistics) || _matching)
            return;
        var wanted = ViewModel.SelectedStatistics;
        if (wanted.Count == StatsGrid.SelectedItems.Count && wanted.All(StatsGrid.SelectedItems.Contains))
            return;
        _matching = true;
        try
        {
            StatsGrid.SelectedItems.Clear();
            foreach (var row in wanted)
                StatsGrid.SelectedItems.Add(row);
        }
        finally
        {
            _matching = false;
        }
    }
}
