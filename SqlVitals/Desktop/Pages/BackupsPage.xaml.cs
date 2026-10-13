using System.Windows.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Backups, bound to <see cref="BackupsViewModel"/> (#40, #184): every database's last backups
/// against the RPO on the page, and the backups msdb keeps for the one selected. All of it is the
/// view model's; the page is only its layout.
/// </summary>
public partial class BackupsPage : Page, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    internal BackupsViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the summary's folds are remembered; none in some tests.</param>
    public BackupsPage(BackupsViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }
}
