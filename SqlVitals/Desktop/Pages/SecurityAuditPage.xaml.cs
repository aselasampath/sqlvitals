using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Security;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// A quick audit of the riskiest security settings (#43): who is sysadmin (or holds CONTROL
/// SERVER), whether the sa login is enabled, databases marked TRUSTWORTHY, and orphaned users.
/// Each finding is a Risk, something to Review, or a Pass, with the risk explained; a risk comes
/// with the T-SQL to remove it, which is never run here.
/// </summary>
public partial class SecurityAuditPage : Page, ICodeBehindPage, IPageContext, IDetailsSource
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

    // The page template's verdict (#150): the risks found, the highest first.
    internal SecurityAuditViewModel ViewModel { get; }

    private const int RisksFilter = 1;

    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private static readonly (string Label, Func<SecurityFinding, bool> Keep, string Empty)[] Filters =
    [
        ("All findings",       _ => true,
            "There are no findings to show."),
        ("Risks",              f => f.IsRisk,
            "Nothing audited widens the attack surface."),
        ("To review",          f => f.Status == SecurityFindingStatus.Review,
            "There is nothing to review."),
        ("sysadmin members",   f => f.Kind == SecurityCheckKind.SysadminMember,
            "There are no sysadmin members to show."),
        ("sa login",           f => f.Kind == SecurityCheckKind.SaLogin,
            "The sa login couldn't be read."),
        ("TRUSTWORTHY",        f => f.Kind == SecurityCheckKind.Trustworthy,
            "There are no databases to show."),
        ("Orphaned users",     f => f.Kind == SecurityCheckKind.OrphanedUser,
            "There are no orphaned users to show."),
        ("Not checked",        f => f.Status == SecurityFindingStatus.NotChecked,
            "Everything could be checked."),
    ];

    private readonly IWaitStatsRepository _repo;
    private readonly string               _serverName;

    // What was read last; changing the filter only filters it again.
    private SecurityFindingList? _findings;

    // Set once the filter choices are filled in, so filling them doesn't filter.
    private readonly bool _ready;

    private bool _refreshing;

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public SecurityAuditPage(IWaitStatsRepository repo, string? serverName, ISectionFolds? folds = null)
    {
        _repo       = repo;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName;
        InitializeComponent();

        foreach (var (label, _, _) in Filters)
            CmbFilter.Items.Add(label);
        CmbFilter.SelectedIndex = 0;

        ViewModel = new SecurityAuditViewModel(showFinding: ShowSubject, script: OpenScript);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        DrawerParts.FollowSelection(FindingsGrid, ShowFinding, _drawer.Clear);

        // Filled before the page has been laid out, the grid can keep every fixed-width column at
        // its 20 px minimum, so findings read that soon are shown once it has been.
        Loaded += (_, _) =>
        {
            if (_findings is not null)
                Dispatcher.BeginInvoke(Show, DispatcherPriority.Background);
        };

        _ready = true;
    }

    /// <summary>Reads and audits. Called on navigate, by the top bar's Refresh and Audit again.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        BtnReload.IsEnabled = false;
        if (_findings is null)
            TxtStatus.Text = "Reading the logins, databases and users…";
        try
        {
            _findings = await _repo.GetSecurityAuditAsync();
            Show();
        }
        catch (Exception ex) when (_findings is null)
        {
            // Nothing read yet: the verdict says why. Later, the results read last stay shown.
            ViewModel.ShowError("the logins, databases and users", ex);
            throw;
        }
        finally
        {
            _refreshing = false;
            BtnReload.IsEnabled = true;
        }
    }

    private void Show()
    {
        if (_findings is not { } list || !IsLoaded) return;

        var filter = Filters[Math.Max(0, CmbFilter.SelectedIndex)];
        var shown  = list.Findings.Where(filter.Keep).ToList();

        // Keep the same finding across a refresh; otherwise the first, a risk if there is one.
        var selected = FindingsGrid.SelectedItem as SecurityFinding;
        DataGridRefresh.SetItemsSource(FindingsGrid, shown);
        FindingsGrid.SelectedItem = shown.FirstOrDefault(f => selected is not null && f.Kind == selected.Kind &&
                                                              f.Scope == selected.Scope && f.Subject == selected.Subject)
                                    ?? shown.FirstOrDefault();

        TxtCount.Text = !list.Available ? "Findings"
            : shown.Count == list.Findings.Count ? $"{list.Findings.Count:N0} finding{(list.Findings.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Findings.Count:N0} findings";

        ViewModel.Show(list);

        var problem = list.Available ? list.Problem : null;
        TxtProblem.Text       = problem ?? string.Empty;
        TxtProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text        = list.Available
            ? $"Audited at {DateTime.Now:HH:mm:ss}. Read from the catalog views; nothing is changed."
            : string.Empty;

        BtnScript.IsEnabled = list.Findings.Any(f => f.IsRisk && f.FixScript is not null);

        TxtNoData.Text       = !list.Available ? list.Problem : filter.Empty;
        TxtNoData.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (FindingsGrid.SelectedItem is { } selectedRow)
            ShowFinding(selectedRow, open: false);
    }

    // The verdict's Show it: the finding it names, selected (with the risks listed if the
    // filter hides it), in view and in the drawer.
    private void ShowSubject()
    {
        if (_findings is null || SecurityAuditVerdict.Subject(_findings) is not { } subject)
            return;
        if (!FindingsGrid.Items.Contains(subject))
            CmbFilter.SelectedIndex = RisksFilter;
        if (!FindingsGrid.Items.Contains(subject))
        {
            CmbFilter.SelectedIndex = 0;
            FindingsFilter.Clear();
        }
        FindingsGrid.SelectedItem = subject;
        FindingsGrid.ScrollIntoView(subject);
        ShowFinding(subject, open: true);
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Show();
    }

    // As the top bar's Refresh: the view model reads, and reports how it went (#169).
    private async void BtnReload_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private void BtnScript_Click(object sender, RoutedEventArgs e) => OpenScript();

    // Script the risks, from the button or the verdict.
    private void OpenScript()
    {
        if (_findings?.FixScript(_serverName, DateTime.Now) is not { } script) return;

        var fixes = _findings.Findings.Count(f => f.IsRisk && f.FixScript is not null);
        CompositionRoot.Get<IDialogService>().ShowSqlScript(new SqlScript(
            "Security Fixes",
            $"Review before running. {fixes:N0} change{(fixes == 1 ? "" : "s")}, one per risk. Removing access can stop an " +
            "application or a person from working, so check first who uses each login. SqlVitals does not run this script.",
            script,
            $"SecurityFixes_{DateTime.Now:yyyyMMdd_HHmm}.sql"), modal: true);
    }
}
