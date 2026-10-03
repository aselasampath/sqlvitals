using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages;

public partial class ImplicitConversionsPage : System.Windows.Controls.Page, IRefreshable, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

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
