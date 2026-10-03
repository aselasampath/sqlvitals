using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages;

public partial class ActiveWaitsPage : System.Windows.Controls.Page, IRefreshable, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

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
