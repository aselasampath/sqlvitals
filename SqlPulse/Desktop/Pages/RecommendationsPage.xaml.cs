using SqlPulse.Engine.Repositories;

namespace SqlPulse.Desktop.Pages;

public partial class RecommendationsPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public RecommendationsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        RecList.ItemsSource = (await _repo.GetRecommendationsAsync()).ToList();
    }
}
