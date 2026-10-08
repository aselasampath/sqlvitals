using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

public partial class ImplicitConversionsPage : System.Windows.Controls.Page, ICodeBehindPage, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private readonly IWaitStatsRepository _repo;

    // The page template's verdict (#149): how many plans convert implicitly, the most used first.
    internal ImplicitConversionsViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the explanation's folds are remembered; none in some tests.</param>
    public ImplicitConversionsPage(IWaitStatsRepository repo, ISectionFolds? folds = null)
    {
        _repo = repo;
        InitializeComponent();

        ViewModel = new ImplicitConversionsViewModel(showWorst: ShowWorst);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        List<ImplicitConversion> rows;
        try
        {
            rows = (await _repo.GetImplicitConversionsAsync()).ToList();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("the plan cache", ex);
            throw;
        }
        ViewModel.Show(rows);
        ConvGrid.ItemsSource = rows;
    }

    // The most used plan, selected and in view.
    private void ShowWorst()
    {
        if (ConvGrid.ItemsSource is not IEnumerable<ImplicitConversion> rows || rows.OrderByDescending(r => r.ExecutionCount).FirstOrDefault() is not { } worst)
            return;
        ConvGrid.SelectedItem = worst;
        ConvGrid.ScrollIntoView(worst);
        ConvGrid.Focus();
    }
}
