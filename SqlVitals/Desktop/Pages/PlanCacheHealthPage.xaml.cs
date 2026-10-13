using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Plan Cache Health, bound to <see cref="PlanCacheHealthViewModel"/> (#185): the code-behind
/// scrolls the plan the verdict shows into view on the Implicit Conversions tab.
/// </summary>
public partial class PlanCacheHealthPage : System.Windows.Controls.Page, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    internal PlanCacheHealthViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the summary's folds are remembered; none in some tests.</param>
    public PlanCacheHealthPage(PlanCacheHealthViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ViewModel.ShowRow += ShowConversion;
    }

    // The verdict's Show it: the plan in view, and the grid focused for the keyboard.
    private void ShowConversion(object row)
    {
        ConvGrid.ScrollIntoView(row);
        ConvGrid.Focus();
    }
}
