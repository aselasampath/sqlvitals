using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Desktop.Pages;

public partial class StaleStatisticsPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public StaleStatisticsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        StatsGrid.ItemsSource = (await _repo.GetStaleStatisticsAsync()).ToList();
        UpdateScriptButton();
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

        new SqlScriptWindow(
            "Update Statistics",
            notice,
            UpdateStatisticsScript.Build(statistics, sampling, DateTime.Now),
            $"UpdateStatistics_{DateTime.Now:yyyyMMdd_HHmm}.sql")
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }
}
