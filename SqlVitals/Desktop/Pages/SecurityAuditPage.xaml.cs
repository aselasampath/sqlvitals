using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Security;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// A quick audit of the riskiest security settings (#43): who is sysadmin (or holds CONTROL
/// SERVER), whether the sa login is enabled, databases marked TRUSTWORTHY, and orphaned users.
/// Each finding is a Risk, something to Review, or a Pass, with the risk explained; a risk comes
/// with the T-SQL to remove it, which is never run here.
/// </summary>
public partial class SecurityAuditPage : Page, IRefreshable, IPageContext
{
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

    public SecurityAuditPage(IWaitStatsRepository repo, string? serverName)
    {
        _repo       = repo;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName;
        InitializeComponent();

        foreach (var (label, _, _) in Filters)
            CmbFilter.Items.Add(label);
        CmbFilter.SelectedIndex = 0;

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

        ShowSummary(list);

        var problem = list.Available ? list.Problem : null;
        TxtProblem.Text       = problem ?? string.Empty;
        TxtProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text        = list.Available
            ? $"Audited at {DateTime.Now:HH:mm:ss}. Read from the catalog views; nothing is changed."
            : string.Empty;

        BtnScript.IsEnabled = list.Findings.Any(f => f.IsRisk && f.FixScript is not null);

        TxtNoData.Text       = !list.Available ? list.Problem : filter.Empty;
        TxtNoData.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // "2 risks  ·  4 to review  ·  6 passed  ·  1 not checked".
    private void ShowSummary(SecurityFindingList list)
    {
        TxtSummary.Inlines.Clear();
        if (!list.Available)
        {
            TxtSummary.Visibility = Visibility.Collapsed;
            return;
        }
        TxtSummary.Visibility = Visibility.Visible;

        void Add(string text, Brush? brush = null, bool bold = false)
        {
            var run = new Run(text) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            if (brush is not null) run.Foreground = brush;
            TxtSummary.Inlines.Add(run);
        }
        var danger  = (Brush)FindResource("Danger");
        var warning = (Brush)FindResource("Warning");
        var success = (Brush)FindResource("Success");
        var muted   = (Brush)FindResource("TextMuted");

        Add($"{list.RiskCount:N0} risk{(list.RiskCount == 1 ? "" : "s")}", list.RiskCount > 0 ? danger : null, true);
        Add("  ·  ", muted);
        Add($"{list.ReviewCount:N0} to review", list.ReviewCount > 0 ? warning : null);
        Add("  ·  ", muted);
        Add($"{list.PassCount:N0} passed", list.RiskCount == 0 ? success : null);
        if (list.NotCheckedCount > 0)
        {
            Add("  ·  ", muted);
            Add($"{list.NotCheckedCount:N0} not checked", muted);
        }
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Show();
    }

    // The top bar's Refresh reports errors through MainWindow; a read started here reports its own.
    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAsync();
            MainWindow.ReportBackgroundSuccess(this);
        }
        catch (Exception ex)
        {
            AppLog.Error("Security Audit: read failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    private void BtnScript_Click(object sender, RoutedEventArgs e)
    {
        if (_findings?.FixScript(_serverName, DateTime.Now) is not { } script) return;

        var fixes = _findings.Findings.Count(f => f.IsRisk && f.FixScript is not null);
        new SqlScriptWindow(
            "Security Fixes",
            $"Review before running. {fixes:N0} change{(fixes == 1 ? "" : "s")}, one per risk. Removing access can stop an " +
            "application or a person from working, so check first who uses each login. SqlVitals does not run this script.",
            script,
            $"SecurityFixes_{DateTime.Now:yyyyMMdd_HHmm}.sql")
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }

    // ── Selected finding ──────────────────────────────────────────────────────

    private void FindingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FindingsGrid.SelectedItem is not SecurityFinding finding)
        {
            DetailPanel.IsEnabled     = false;
            TxtDetailHeader.Text      = "Select a finding above to see the risk and how to remove it.";
            TxtDetailExplanation.Text = string.Empty;
            FixPanel.Visibility       = Visibility.Collapsed;
            return;
        }

        DetailPanel.IsEnabled = true;
        TxtDetailHeader.Text  = string.Join(" · ", new[] { finding.Name, finding.Scope, finding.Subject, finding.StatusText, finding.Finding }
            .Where(s => s.Length > 0).Distinct());
        TxtDetailExplanation.Text = finding.Explanation;

        TxtFix.Text         = finding.FixScript ?? string.Empty;
        TxtFixLabel.Text    = finding.IsRisk
            ? "To remove the risk: review, then run it yourself. SqlVitals never runs it."
            : "If you decide to change it: review, then run it yourself. SqlVitals never runs it.";
        FixPanel.Visibility = finding.FixScript is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnCopyFix_Click(object sender, RoutedEventArgs e)
    {
        if (TxtFix.Text.Length > 0)
            ClipboardHelper.SetText(TxtFix.Text);
    }
}
