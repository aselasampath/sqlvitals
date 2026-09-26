using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// The server's configuration against best practice (#42): MAXDOP, cost threshold for
/// parallelism, max server memory, TempDB's data files, and each database's auto-shrink, page
/// verify and compatibility level. Each check shows Pass or Warn, the value now, the value
/// recommended and why; a warning comes with the T-SQL to fix it, which is never run here.
/// </summary>
public partial class ConfigChecksPage : Page, IRefreshable
{
    private static readonly (string Label, Func<ConfigCheck, bool> Keep, string Empty)[] Filters =
    [
        ("All checks",         _ => true,
            "There are no checks to show."),
        ("Warnings",           c => c.IsWarning,
            "Every setting checked is what best practice recommends."),
        ("Server settings",    c => !c.IsDatabaseCheck,
            "There are no server settings to show."),
        ("Database settings",  c => c.IsDatabaseCheck,
            "There are no database settings to show."),
        ("Not checked",        c => c.Status == ConfigCheckStatus.NotChecked,
            "Every check could be made."),
    ];

    private readonly IWaitStatsRepository _repo;
    private readonly string               _serverName;

    // What was read last; changing the filter only filters it again.
    private ConfigCheckList? _checks;

    // Set once the filter choices are filled in, so filling them doesn't filter.
    private readonly bool _ready;

    private bool _refreshing;

    public ConfigChecksPage(IWaitStatsRepository repo, string? serverName)
    {
        _repo       = repo;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName;
        InitializeComponent();

        foreach (var (label, _, _) in Filters)
            CmbFilter.Items.Add(label);
        CmbFilter.SelectedIndex = 0;

        // Filled before the page has been laid out, the grid can keep every fixed-width column at
        // its 20 px minimum, so checks read that soon are shown once it has been.
        Loaded += (_, _) =>
        {
            if (_checks is not null)
                Dispatcher.BeginInvoke(Show, DispatcherPriority.Background);
        };

        _ready = true;
    }

    /// <summary>Reads the settings and checks them. Called on navigate, by the sidebar Refresh and Check again.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        BtnReload.IsEnabled = false;
        if (_checks is null)
            TxtStatus.Text = "Reading the server's configuration…";
        try
        {
            _checks = await _repo.GetConfigurationChecksAsync();
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
        if (_checks is not { } list || !IsLoaded) return;

        var filter = Filters[Math.Max(0, CmbFilter.SelectedIndex)];
        var shown  = list.Checks.Where(filter.Keep).ToList();

        // Keep the same check across a refresh; otherwise the first, a warning if there is one.
        var selected = ChecksGrid.SelectedItem as ConfigCheck;
        DataGridRefresh.SetItemsSource(ChecksGrid, shown);
        ChecksGrid.SelectedItem = shown.FirstOrDefault(c => selected is not null && c.Kind == selected.Kind && c.Scope == selected.Scope)
                                  ?? shown.FirstOrDefault();

        TxtCount.Text = !list.Available ? "Checks"
            : shown.Count == list.Checks.Count ? $"{list.Checks.Count:N0} result{(list.Checks.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Checks.Count:N0} results";

        ShowSummary(list);

        var problem = list.Available ? list.Problem : null;
        TxtProblem.Text       = problem ?? string.Empty;
        TxtProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text        = list.Available
            ? $"Checked at {DateTime.Now:HH:mm:ss}. Settings are read as the server is using them now; nothing is changed."
            : string.Empty;

        BtnScript.IsEnabled = list.Checks.Any(c => c.IsWarning && c.FixScript is not null);

        TxtNoData.Text       = !list.Available ? list.Problem : filter.Empty;
        TxtNoData.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // "3 warnings  ·  11 passed  ·  1 not checked".
    private void ShowSummary(ConfigCheckList list)
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
        var warning = (Brush)FindResource("Warning");
        var success = (Brush)FindResource("Success");
        var muted   = (Brush)FindResource("TextMuted");

        Add($"{list.WarnCount:N0} warning{(list.WarnCount == 1 ? "" : "s")}", list.WarnCount > 0 ? warning : null, true);
        Add("  ·  ", muted);
        Add($"{list.PassCount:N0} passed", list.WarnCount == 0 ? success : null);
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

    // The sidebar Refresh reports errors through MainWindow; a read started here reports its own.
    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Config Checks: read failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    private void BtnScript_Click(object sender, RoutedEventArgs e)
    {
        if (_checks?.FixScript(_serverName, DateTime.Now) is not { } script) return;

        var fixes = _checks.Checks.Count(c => c.IsWarning && c.FixScript is not null);
        new SqlScriptWindow(
            "Configuration Fixes",
            $"Review before running. {fixes:N0} change{(fixes == 1 ? "" : "s")}, one per warning. Server settings take effect at " +
            "RECONFIGURE; a compatibility level change can change query plans, so turn Query Store on first. " +
            "SqlVitals does not run this script.",
            script,
            $"ConfigurationFixes_{DateTime.Now:yyyyMMdd_HHmm}.sql")
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }

    // ── Selected check ────────────────────────────────────────────────────────

    private void ChecksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChecksGrid.SelectedItem is not ConfigCheck check)
        {
            DetailPanel.IsEnabled       = false;
            TxtDetailHeader.Text        = "Select a check above to see why it matters and how to fix it.";
            TxtDetailExplanation.Text   = string.Empty;
            FixPanel.Visibility         = Visibility.Collapsed;
            return;
        }

        DetailPanel.IsEnabled = true;
        TxtDetailHeader.Text  = $"{check.Name} · {check.Scope} · {check.StatusText}" +
                                (check.Current.Length > 0 ? $" · {check.Current}" : "") +
                                (check.Recommended.Length > 0 ? $", recommended {check.Recommended}" : "");
        TxtDetailExplanation.Text = check.Explanation;

        TxtFix.Text         = check.FixScript ?? string.Empty;
        FixPanel.Visibility = check.FixScript is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnCopyFix_Click(object sender, RoutedEventArgs e)
    {
        if (TxtFix.Text.Length > 0)
            ClipboardHelper.SetText(TxtFix.Text);
    }
}
