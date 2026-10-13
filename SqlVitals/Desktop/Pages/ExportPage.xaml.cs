using System.Windows.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Export / AI, bound to <see cref="ExportViewModel"/> (#186): the sections to write, where, and
/// the export. All of it is the view model's; the page is only its layout.
/// </summary>
public partial class ExportPage : Page, IPageContext
{
    // Builds a report to save or paste into an AI chat: not a view of the server's data (#140).
    public PageKind Kind => PageKind.None;

    internal ExportViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the explanation's folds are remembered; none in some tests.</param>
    public ExportPage(ExportViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }
}
