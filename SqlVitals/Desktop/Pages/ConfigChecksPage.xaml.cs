using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Config Checks, bound to <see cref="ConfigChecksViewModel"/> (#42, #186): the code-behind fills
/// the details drawer with the check selected, and scrolls the one the verdict shows into view.
/// </summary>
public partial class ConfigChecksPage : Page, IPageContext, IDetailsSource
{
    // ── The details drawer (#150): the selected check ─────────────────────────────
    private const string SelectHint = "Select a check to see why it matters and the script that fixes it.";

    private readonly RowDrawer _drawer = new("Check details", SelectHint);

    public DrawerContent Details => _drawer.Content;

    public event Action<bool>? DetailsChanged
    {
        add    => _drawer.Changed += value;
        remove => _drawer.Changed -= value;
    }

    internal RowDetailsView? DetailsView => _drawer.View;

    private void ShowCheck(object row, bool open)
    {
        if (row is ConfigCheck check)
            _drawer.Show($"{check.Name} · {check.Scope}", RowDetails.Rows(check), open,
                         "To fix it: review, then run it yourself. SqlVitals never runs it.", check.FixScript);
    }

    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    internal ConfigChecksViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public ConfigChecksPage(ConfigChecksViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        DrawerParts.FollowSelection(ChecksGrid, ShowCheck, _drawer.Clear);
        ViewModel.ShowRow += ShowSubject;
    }

    // The verdict's Show it: past a search that hides it, in view and in the drawer.
    private void ShowSubject(ConfigCheck check)
    {
        if (!ChecksGrid.Items.Contains(check))
            ChecksFilter.Clear();
        ChecksGrid.SelectedItem = check;
        ChecksGrid.ScrollIntoView(check);
        ShowCheck(check, open: true);
    }
}
