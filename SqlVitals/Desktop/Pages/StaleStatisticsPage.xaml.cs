using System.Windows;
using System.Windows.Controls;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Scripting;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Pages;

public partial class StaleStatisticsPage : System.Windows.Controls.Page, IRefreshable, IPageContext
{
    // Read once, and again on Refresh, from the connection's database (#140).
    public PageKind Kind => PageKind.Snapshot;
    public bool UsesDatabase => true;

    private readonly IWaitStatsRepository _repo;

    // The page template's verdict (#150): the statistic with the most of its table changed.
    internal StaleStatisticsViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the explanation's folds are remembered; none in some tests.</param>
    public StaleStatisticsPage(IWaitStatsRepository repo, ISectionFolds? folds = null)
    {
        _repo = repo;
        InitializeComponent();

        ViewModel = new StaleStatisticsViewModel(showWorst: ShowWorst);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        List<StaleStatistic> stats;
        try
        {
            stats = (await _repo.GetStaleStatisticsAsync()).ToList();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("the statistics", ex);
            throw;
        }
        ViewModel.Show(stats);
        _stats = stats;
        StatsGrid.ItemsSource = stats;
        UpdateScriptButton();
    }

    // What the verdict judged, so Show it selects the statistic it names.
    private IReadOnlyList<StaleStatistic> _stats = [];

    // The verdict's Show it: the most changed statistic, selected and in view.
    private void ShowWorst()
    {
        if (StaleStatsVerdict.Worst(_stats) is not { } worst)
            return;
        StatsGrid.SelectedItem = worst;
        StatsGrid.ScrollIntoView(worst);
    }

    private void StatsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateScriptButton();

    private void UpdateScriptButton()
    {
        int selected = StatsGrid.SelectedItems.Count;
        int total    = StatsGrid.Items.Count;

        BtnUpdateStatsScript.IsEnabled = total > 0;
        BtnUpdateStatsScript.Content = selected > 0
            ? $"Generate UPDATE STATISTICS Script ({selected} selected)"
            : "Generate UPDATE STATISTICS Script (all)";
    }

    private void BtnUpdateStatsScript_Click(object sender, RoutedEventArgs e)
    {
        // Selected rows if the user picked some, otherwise everything in the grid —
        // in the grid's current sort order so the script reads like what's on screen.
        var selected = StatsGrid.SelectedItems.OfType<StaleStatistic>().ToHashSet();
        var statistics = StatsGrid.Items.OfType<StaleStatistic>()
            .Where(s => selected.Count == 0 || selected.Contains(s))
            .ToList();
        if (statistics.Count == 0) return;

        var sampling = RbFullScan.IsChecked == true ? StatisticsSampling.FullScan : StatisticsSampling.Default;
        var summary  = UpdateStatisticsScript.Summarize(statistics);
        var notice =
            $"Review before running. {summary.Statistics:N0} statistic(s) on {summary.Tables:N0} table(s) will be updated " +
            (sampling == StatisticsSampling.FullScan
                ? $"WITH FULLSCAN, reading about {summary.RowsInTables:N0} rows — run it off-peak."
                : "with default sampling.") +
            " Plans that use them recompile on their next run. SqlVitals does not run this script.";

        CompositionRoot.Get<IDialogService>().ShowSqlScript(new SqlScript(
            "Update Statistics",
            notice,
            UpdateStatisticsScript.Build(statistics, sampling, DateTime.Now),
            $"UpdateStatistics_{DateTime.Now:yyyyMMdd_HHmm}.sql"), modal: true);
    }
}
