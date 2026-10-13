using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Security;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Security Audit, bound to <see cref="SecurityAuditViewModel"/> (#43, #186): the code-behind fills
/// the details drawer with the finding selected, and scrolls the one the verdict shows into view.
/// </summary>
public partial class SecurityAuditPage : Page, IPageContext, IDetailsSource
{
    // ── The details drawer (#150): the selected finding ─────────────────────────────
    private const string SelectHint = "Select a finding to see the risk and the script that removes it.";

    private readonly RowDrawer _drawer = new("Finding details", SelectHint);

    public DrawerContent Details => _drawer.Content;

    public event Action<bool>? DetailsChanged
    {
        add    => _drawer.Changed += value;
        remove => _drawer.Changed -= value;
    }

    internal RowDetailsView? DetailsView => _drawer.View;

    private void ShowFinding(object row, bool open)
    {
        if (row is not SecurityFinding finding)
            return;
        var heading = finding.IsRisk
            ? "To remove the risk: review, then run it yourself. SqlVitals never runs it."
            : "If you decide to change it: review, then run it yourself. SqlVitals never runs it.";
        _drawer.Show($"{finding.Name} · {finding.Subject}", RowDetails.Rows(finding), open, heading, finding.FixScript);
    }

    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    internal SecurityAuditViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public SecurityAuditPage(SecurityAuditViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        DrawerParts.FollowSelection(FindingsGrid, ShowFinding, _drawer.Clear);
        ViewModel.ShowRow += ShowSubject;
    }

    // The verdict's Show it: past a search that hides it, in view and in the drawer.
    private void ShowSubject(SecurityFinding finding)
    {
        if (!FindingsGrid.Items.Contains(finding))
            FindingsFilter.Clear();
        FindingsGrid.SelectedItem = finding;
        FindingsGrid.ScrollIntoView(finding);
        ShowFinding(finding, open: true);
    }
}
