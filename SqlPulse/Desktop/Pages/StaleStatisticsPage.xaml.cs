using SqlPulse.Engine.Repositories;

namespace SqlPulse.Desktop.Pages;

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
    }
}
