using System.Windows.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// DB Storage, bound to <see cref="DatabaseStorageViewModel"/> (#185): the code-behind scrolls the
/// file the verdict shows into view, and hands an alert's focus (#145) to the view model.
/// </summary>
public partial class DatabaseStoragePage : Page, IPageContext, IFocusPage
{
    // Read once, and again on Refresh, from the connection's database (#140).
    public PageKind Kind => PageKind.Snapshot;
    public bool UsesDatabase => true;

    internal DatabaseStorageViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the sizes' folds are remembered; none in some tests.</param>
    public DatabaseStoragePage(DatabaseStorageViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ViewModel.ShowRow += GridFiles.ScrollIntoView;
    }

    // ── A log space alert's evidence (#145): the view model lists the database's files only ──

    public void ShowFocus(PageFocus focus)
    {
        if (focus.Filter is { Kind: FocusKind.Database, Name: { } name })
            ViewModel.FocusOn(name);
    }

    public void ClearFocus() => ViewModel.FocusOn(null);

    public string? FocusNote => ViewModel.FocusNote;

    public string? ShowAllText => ViewModel.ShowAllText;
}
