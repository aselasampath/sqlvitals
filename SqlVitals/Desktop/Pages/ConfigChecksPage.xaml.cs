using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// The server's configuration against best practice (#42): MAXDOP, cost threshold for
/// parallelism, max server memory, TempDB's data files, and each database's auto-shrink, page
/// verify and compatibility level. Each check shows Pass or Warn, the value now, the value
/// recommended and why; a warning comes with the T-SQL to fix it, which is never run here.
/// </summary>
public partial class ConfigChecksPage : Page, ICodeBehindPage, IPageContext, IDetailsSource
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

    // The page template's verdict (#150): how many checks fail, the riskiest first.
    internal ConfigChecksViewModel ViewModel { get; }

    private const int RisksFilter = 1;

    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

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

    // The top bar: what this page read, what went wrong, and its kind or window (#166).
    private readonly IShellStatus _shell;

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public ConfigChecksPage(IWaitStatsRepository repo, string? serverName, ISectionFolds? folds = null, IShellStatus? shell = null)
    {
        _shell      = shell ?? CompositionRoot.Get<IShellStatus>();
        _repo       = repo;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName;
        InitializeComponent();

        foreach (var (label, _, _) in Filters)
            CmbFilter.Items.Add(label);
        CmbFilter.SelectedIndex = 0;

        ViewModel = new ConfigChecksViewModel(showCheck: ShowSubject, script: OpenScript);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        DrawerParts.FollowSelection(ChecksGrid, ShowCheck, _drawer.Clear);

        // Filled before the page has been laid out, the grid can keep every fixed-width column at
        // its 20 px minimum, so checks read that soon are shown once it has been.
        Loaded += (_, _) =>
        {
            if (_checks is not null)
                Dispatcher.BeginInvoke(Show, DispatcherPriority.Background);
        };

        _ready = true;
    }

    /// <summary>Reads the settings and checks them. Called on navigate, by the top bar's Refresh and Check again.</summary>
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
        catch (Exception ex) when (_checks is null)
        {
            // Nothing read yet: the verdict says why. Later, the results read last stay shown.
            ViewModel.ShowError("the server's configuration", ex);
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
        if (_checks is not { } list || !IsLoaded) return;

        var filter = Filters[Math.Max(0, CmbFilter.SelectedIndex)];
        var shown  = list.Checks.Where(filter.Keep).ToList();

        // Keep the same check across a refresh; otherwise the first, a warning if there is one.
        var selected = ChecksGrid.SelectedItem as ConfigCheck;
        DataGridRefresh.SetItemsSource(ChecksGrid, shown);
        ChecksGrid.SelectedItem = shown.FirstOrDefault(c => selected is not null && c.Kind == selected.Kind && c.Scope == selected.Scope)
                                  ?? shown.FirstOrDefault();

        TxtCount.Text = shown.Count == list.Checks.Count
            ? $"{list.Checks.Count:N0} result{(list.Checks.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Checks.Count:N0} results";

        ViewModel.Show(list);

        TxtProblem.Text       = list.Problem ?? string.Empty;
        TxtProblem.Visibility = list.Problem is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text        = $"Checked at {DateTime.Now:HH:mm:ss}. Settings are read as the server is using them now; nothing is changed.";

        BtnScript.IsEnabled = list.Checks.Any(c => c.IsWarning && c.FixScript is not null);

        TxtNoData.Text       = filter.Empty;
        TxtNoData.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ChecksGrid.SelectedItem is { } selectedRow)
            ShowCheck(selectedRow, open: false);
    }

    // The verdict's Show it: the check it names, selected (with the warnings listed if the
    // filter hides it), in view and in the drawer.
    private void ShowSubject()
    {
        if (_checks is null || ConfigChecksVerdict.Riskiest(_checks) is not { } subject)
            return;
        if (!ChecksGrid.Items.Contains(subject))
            CmbFilter.SelectedIndex = RisksFilter;
        if (!ChecksGrid.Items.Contains(subject))
        {
            CmbFilter.SelectedIndex = 0;
            ChecksFilter.Clear();
        }
        ChecksGrid.SelectedItem = subject;
        ChecksGrid.ScrollIntoView(subject);
        ShowCheck(subject, open: true);
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Show();
    }

    // The top bar's Refresh reports errors through the view model; a read started here reports its own.
    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAsync();
            _shell.ReportRead(this, PageRead.Timed);
        }
        catch (Exception ex)
        {
            AppLog.Error("Config Checks: read failed", ex);
            TxtStatus.Text = ErrorStatus.Format(ex);
        }
    }

    private void BtnScript_Click(object sender, RoutedEventArgs e) => OpenScript();

    // Script the warnings, from the button or the verdict.
    private void OpenScript()
    {
        if (_checks?.FixScript(_serverName, DateTime.Now) is not { } script) return;

        var fixes = _checks.Checks.Count(c => c.IsWarning && c.FixScript is not null);
        CompositionRoot.Get<IDialogService>().ShowSqlScript(new SqlScript(
            "Configuration Fixes",
            $"Review before running. {fixes:N0} change{(fixes == 1 ? "" : "s")}, one per warning. Server settings take effect at " +
            "RECONFIGURE; a compatibility level change can change query plans, so turn Query Store on first. " +
            "SqlVitals does not run this script.",
            script,
            $"ConfigurationFixes_{DateTime.Now:yyyyMMdd_HHmm}.sql"), modal: true);
    }
}
