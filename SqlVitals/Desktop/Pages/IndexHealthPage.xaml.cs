using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class IndexHealthPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public IndexHealthPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        double.TryParse(TxtMinFragPct.Text, out double minFrag);
        long.TryParse(TxtMinPageCount.Text, out long minPages);
        if (minFrag <= 0) minFrag = 10;
        if (minPages <= 0) minPages = 1000;

        var (missing, unused) = await _repo.GetIndexHealthAsync();
        var usage = await _repo.GetIndexUsagePatternsAsync();
        var fragmentation = await _repo.GetIndexFragmentationAsync(minFrag, minPages);

        MissingGrid.ItemsSource = missing
            .OrderBy(m => m.Severity == "CRITICAL" ? 1 : m.Severity == "WARNING" ? 2 : 3)
            .ThenByDescending(m => m.ImpactScore)
            .ToList();
        UnusedGrid.ItemsSource  = unused.OrderByDescending(u => u.UserUpdates).ToList();
        UsageGrid.ItemsSource   = usage.ToList();
        FragGrid.ItemsSource    = fragmentation.ToList();
    }
}
