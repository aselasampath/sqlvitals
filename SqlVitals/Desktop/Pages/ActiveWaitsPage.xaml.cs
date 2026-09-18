using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class ActiveWaitsPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public ActiveWaitsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        ActiveGrid.ItemsSource = (await _repo.GetActiveWaitsAsync()).ToList();
    }
}
