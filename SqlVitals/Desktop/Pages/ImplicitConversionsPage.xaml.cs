using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class ImplicitConversionsPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public ImplicitConversionsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        ConvGrid.ItemsSource = (await _repo.GetImplicitConversionsAsync()).ToList();
    }
}
