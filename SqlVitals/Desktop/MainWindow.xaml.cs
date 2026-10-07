using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop;

[SuppressMessage("Design", "CA1001", Justification = "A Window isn't IDisposable; OnClosing disposes _tray, Monitoring and Chat")]
public partial class MainWindow : Window
{
    // Replaced whenever the active connection changes, so pages created after that point
    // query the new server without restarting the app.
    internal IWaitStatsRepository Repo { get; private set; }
    internal readonly ConnectionSettingsService SettingsService;
    private bool _isConnectionConfigured;

    // Identifies the connection Repo was built from, so re-reading the store only swaps the
    // repository when the active connection (or its details) actually changed.
    private Guid? _activeConnectionId;
    private string _activeFingerprint = string.Empty;

    // The pages visited, for Back and Forward; its current route is the page on screen, reopened
    // against the new database after a switch (#139).
    private readonly NavigationHistory _history = new();

    // How each recent route's page was being looked at, put back when it's opened again (#139).
    private readonly RouteStates<ViewState> _viewStates = new();

    // The routes opened this session, most recent first, for the quick switcher (#144).
    private readonly RecentRoutes _recent = new();

    // Set from Back or Forward until the move is recorded, so a second press doesn't pick the
    // same target from a history that hasn't moved yet.
    private bool _historyMovePending;

    // The page shown for the current route, and whether it has finished loading: one left
    // before then hasn't had its saved view state put back yet, so that state is kept.
    private Page? _page;
    private bool  _pageLoaded;

    // When the page on screen last loaded, refreshed or reported an update: a snapshot's "Taken",
    // a live page's "As of" (#140). Null until its first load finishes.
    private DateTime? _updatedAt;

    // The Settings category last opened, so Settings opens on it again this session (#131).
    private SettingsCategory _settingsCategory = SettingsCategory.Connections;

    // Bumped on every navigation and connection switch. A page load that finishes after
    // either has happened belongs to a previous database, so its result is discarded.
    private int _loadVersion;


    // Messages and the app's windows (#182).
    private readonly IDialogService _dialogs = CompositionRoot.Get<IDialogService>();

    // Background live-metrics collection for the active and every monitored connection.
    internal readonly MonitoringManager Monitoring = new();

    // The Slack and Teams channels in Settings → Alerts & chat (#108).
    internal readonly ChatNotificationService Chat = new(new HttpWebhookSender(), ui: System.Threading.SynchronizationContext.Current
                                                                              ?? new System.Windows.Threading.DispatcherSynchronizationContext());
    private List<ConnectionSelectorItem> _selectorItems = new();

    // The notification-area icon: Windows notifications for alerts, and the way back to the
    // window while it's hidden (#35).
    private readonly TrayIcon _tray;

    // What restoring from the tray goes back to: normal or maximized.
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"SqlVitals v{version?.Major}.{version?.Minor}.{version?.Build}";
        Shell.VersionText.Text = $"v{version?.Major}.{version?.Minor}.{version?.Build}";

        // Pages are recreated on each navigation, but the Frame journal would otherwise keep
        // the old instances — and the mouse Back button could bring back a page still showing
        // data from the previous database.
        MainFrame.Navigated += (_, _) =>
        {
            while (MainFrame.CanGoBack)
                MainFrame.RemoveBackEntry();
        };

        Bar.BackRequested     += async () => await GoAsync(back: true);
        Bar.ForwardRequested  += async () => await GoAsync(back: false);
        Bar.RefreshRequested  += async () => await RefreshPageAsync();
        Bar.PauseRequested    += PausePage;
        Bar.CrumbRequested    += async route => await OpenAsync(route, settingsMessage: null, _history.Visit);
        Bar.ServerRequested   += async id => await SwitchServerAsync(id);
        Bar.DatabaseRequested += async () =>
        {
            if (_activeConnectionId is { } id)
                await NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id));
        };

        // Ctrl+K, or the bar's search button (#144). What it opens goes through the navigator, so
        // Back returns; the keyboard goes into the new page if it was in the one replaced, unless
        // something else has taken it while the page loaded.
        Bar.JumpRequested += OpenSwitcher;
        Switcher.PageHost = MainFrame;
        Switcher.OpenRequested += async (route, intoPage) =>
        {
            await OpenAsync(route, settingsMessage: null, _history.Visit, ActivitySources.QuickSwitcher);
            if (intoPage && (Keyboard.FocusedElement is not DependencyObject focused || ReferenceEquals(focused, this) || Switcher.IsAncestorOf(focused)))
                MainFrame.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        };

        // A page opened as an alert's evidence (#145): Show all clears its filter where it is, and the
        // banner's history link opens the page that charts the alert's window.
        FocusBar.ShowAllRequested += ShowAllEvidence;
        FocusBar.HistoryRequested += async () =>
        {
            if (_history.Current is { } current && EvidenceRoutes.History(current) is { } history)
                await OpenAsync(history, settingsMessage: null, _history.Visit, ActivitySources.Sidebar);
        };

        SettingsService = new ConnectionSettingsService();
        _overviewVisits = new OverviewVisits(SettingsService);
        var store  = SettingsService.Load();

        // Every grid's density, as chosen in Settings → Appearance (#148).
        if (Application.Current is { } app)
            GridDensityResources.Apply(app.Resources, store.GridDensity);

        // The details drawer (#147): pinned or floating and its width as last left; closed at start.
        Details.Layout = new DrawerLayout(store.DrawerPinned, store.DrawerWidth);
        Details.PreferencesChanged += SaveDrawer;
        Details.StateChanged       += () => Bar.ShowDetailsOpen(Details.State != DrawerState.Closed);
        Bar.DetailsRequested       += Details.Toggle;
        Bar.FocusRequested         += ToggleFocus;
        var active = store.Active;
        BuildSidebar(store);
        Repo = BuildRepository(active, store.CommandTimeoutSeconds);

        _tray = new TrayIcon(SettingsService);
        _tray.OpenRequested   += ShowFromTray;
        // The tray menu's Alerts opens every server's; a notification opens its server's workspace (#141).
        _tray.AlertsRequested += async connectionId =>
        {
            ShowFromTray();
            if (connectionId is { } id && Item(id) is not null)
                await NavigateTo("Overview", serverId: id);
            else
                await NavigateTo("Alerts", alertsConnectionId: connectionId);
        };
        // A notification about one alert opens its evidence (#145).
        _tray.EvidenceRequested += async alert =>
        {
            ShowFromTray();
            await OpenEvidenceAsync(alert, ActivitySources.Notification);
        };
        _tray.ExitRequested   += ExitApp;
        _tray.StartAllRequested += () => Monitoring.StartAll(ActivitySources.TrayMenu);
        _tray.StopAllRequested  += () => Monitoring.StopAll(ActivitySources.TrayMenu);

        // Every settings change goes to the activity log (#115); never a secret (see SettingsDiff).
        SettingsService.Changed += rows =>
        {
            foreach (var row in rows)
                Monitoring.Log(row);

            // Chat channels, and the connection names their messages use, apply from the next
            // alert. Only reads (see Apply).
            Chat.Apply(SettingsService.Load());
        };

        Chat.Apply(store);
        Chat.StartedFailing += OnChatChannelFailing;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (store.ChatChannelsDropped)
            _tray.Inform("Chat channels were removed",
                "SqlVitals couldn't read its Slack and Teams channels on this PC or Windows account, so alerts " +
                "can't be posted to them. Add them again in Settings → Alerts & chat.");

        Monitoring.SessionStateChanged += OnMonitoringStateChanged;
        Monitoring.MonitoringChanged   += OnMonitoringChanged;
        Monitoring.AlertsChanged       += OnAlertsChanged;
        // Separate from the Windows notification: muting a connection doesn't stop its channels (#121).
        Monitoring.AlertsRaised        += Chat.OnAlertsRaised;
        Monitoring.AllStopped          += Chat.OnAllStopped;
        SyncConnections(store, ActivitySources.AutomaticAtStartup);

        Loaded += async (_, _) =>
        {
            if (!_isConnectionConfigured)
            {
                // First run, or the active connection was removed: send the user to Settings
                // instead of letting every page fail against an empty connection string.
                await NavigateTo("Settings", store.Connections.Count == 0
                    ? "Welcome to SqlVitals! Click + New connection to add your SQL Server, then Save & make active."
                    : "No server is open. Open one from All servers, or add a new connection.",
                    new SettingsLocation(SettingsCategory.Connections));
                return;
            }

            if (active is { NeedsPassword: true })
            {
                // "Remember password" was off last session, so every query would fail the login.
                await NavigateTo("Settings", PasswordPrompt(active), SettingsLocation.Connection(active.Id));
                return;
            }

            await DisablePagesAzureSqlLacksAsync();
            await OpenAsync(StartRoute(store), settingsMessage: null, _history.Visit);
        };
    }

    /// <summary>
    /// Where SqlVitals opens (#141): the page open when it last closed, inside the server it was
    /// inside. A server's page made its server the active connection, so it opens on that. Before
    /// anything was saved, Home with more than one server (#130), the server's Overview with one (#143).
    /// </summary>
    private Route StartRoute(ConnectionStore store)
    {
        // A connection from appsettings.json only has no Overview: Live charts, as before.
        if (store.LastPage is not { } tag)
            return RouteFor(store.Connections.Count >= 2 ? "Home" : _activeConnectionId is null ? "LiveMetrics" : "Overview");
        if (tag == "Overview")
            return store.LastServerId is { } id && store.Connections.Any(c => c.Id == id) ? Workspace.Overview(id) : RouteFor("Home");
        return RouteFor(tag);
    }

    // ── Sidebar and the server workspace (#124, #141) ─────────────────────────

    // The pinned sidebar's buttons, built from NavMenu with the shell, before anything can update one.
    private NavSidebar _sidebar = null!;

    private void BuildSidebar(ConnectionStore store)
    {
        _sidebar = Shell.Menu;
        // A server's page opens on the server whose pages are shown, switching to it if need be.
        Shell.PageRequested          += async tag => await NavigateTo(tag, serverId: WorkspaceServer, switchSource: ActivitySources.Sidebar);
        Shell.ScoreRequested         += ShowWorkspaceScore;
        Shell.MonitorToggleRequested += ToggleWorkspaceMonitoring;

        // Pinned, collapsed or hidden (#142): as last chosen, collapsed in a narrow window. ☰ and
        // Ctrl+B change it, and the top bar's ☰ says what it does next.
        Shell.ReturnFocusTo = Bar.BtnSidebar;
        Shell.ModeShown    += ShowSidebarState;
        Shell.ModeChosen   += SaveSidebarMode;
        Bar.SidebarRequested += Shell.ToggleFromBar;
        Shell.Restore(store.SidebarMode, Width);
        SizeChanged += (_, e) => Shell.SetWindowWidth(e.NewSize.Width);
    }

    private void ShowSidebarState() =>
        Bar.ShowSidebar(Shell.Shown, Shell.IsSlidOver, Monitoring.ActiveAlerts.Count);

    // ── Focus mode (#148) ─────────────────────────────────────────────────────

    // The window as it was before focus mode; null when not in it.
    private WindowPlacement? _beforeFocus;

    internal bool InFocus => _beforeFocus is not null;

    // F11, the Focus button, or Exit focus.
    private void ToggleFocus()
    {
        if (InFocus) ExitFocus();
        else         EnterFocus();
    }

    // Full screen without the sidebar; a floating drawer closes, a pinned one stays. The top bar stays,
    // with Exit focus. Nothing is saved: the sidebar's chosen state is kept to come back to.
    private void EnterFocus()
    {
        if (InFocus)
            return;
        _beforeFocus = WindowPlacement.Capture(this);
        Shell.InFocus = true;
        if (Details.State == DrawerState.Floating)
            Details.Close();
        _beforeFocus.FullScreen().ApplyTo(this);
        Bar.ShowFocus(true);
    }

    // Back to the window and the sidebar as they were.
    private void ExitFocus()
    {
        if (_beforeFocus is not { } before)
            return;
        _beforeFocus  = null;
        Shell.InFocus = false;
        before.ApplyTo(this);
        Bar.ShowFocus(false);
    }

    // Layout preferences: saved, but not in the activity log.
    private void SaveDrawer(bool pinned, double width)
    {
        try { SettingsService.SetDrawer(pinned, width); }
        catch (Exception ex) { AppLog.Error("Details drawer: could not save whether it is pinned, or its width", ex); }
    }

    // ── The details drawer (#147) ─────────────────────────────────────────────

    // The page filling the drawer, while it is the page on screen.
    private IDetailsSource? _detailsSource;

    // A new page: the drawer shows its details, or says it has none; it stays open or closed.
    private void ShowDetailsOf(object page)
    {
        _detailsSource?.DetailsChanged -= OnDetailsChanged;
        _detailsSource = page as IDetailsSource;
        _detailsSource?.DetailsChanged += OnDetailsChanged;
        Details.Show(_detailsSource?.Details ?? DrawerContent.None);
    }

    // The page's selection changed; a click on a row opens a closed drawer.
    private void OnDetailsChanged(bool open)
    {
        if (_detailsSource is null)
            return;
        Details.Show(_detailsSource.Details);
        if (open)
            Details.Open();
    }

    // A layout preference: saved, but not in the activity log (see SettingsActivity).
    private void SaveSidebarMode(SidebarMode mode)
    {
        // Pinned from the menu slid in over a focus-mode page: the user wants it, so focus ends.
        ExitFocus();
        try { SettingsService.SetSidebarMode(mode); }
        catch (Exception ex) { AppLog.Error("Sidebar: could not save whether it is pinned, collapsed or hidden", ex); }
    }

    /// <summary>
    /// The server whose workspace is open: the one the page on screen is inside. Null on a page
    /// about every server (Home, Alerts, Settings).
    /// </summary>
    private Guid? WorkspaceServer => _history.Current is { } route ? Workspace.ServerOf(route) : null;

    private ConnectionSelectorItem? Item(Guid? id) => id is null ? null : _selectorItems.FirstOrDefault(i => i.Settings.Id == id);

    // The sidebar's server part follows the page: its server's header and pages, or how to open one.
    private void ShowWorkspace()
    {
        Shell.ShowServer(Item(WorkspaceServer));
        ApplyPagesAzureSqlLacks();
    }

    // Start or Stop in the server header: the same as ▶ / ■ on its Home tile.
    private void ToggleWorkspaceMonitoring()
    {
        if (WorkspaceServer is not { } id)
            return;
        if (Monitoring.IsMonitoring(id))
            Monitoring.Stop(id, ActivitySources.Sidebar);
        else
            Monitoring.Start(id, ActivitySources.Sidebar);
    }

    // Another server from the breadcrumb's menu: the same page there, or its Overview if it
    // hasn't got it (see OpenAsync). Settings is opened to ask for a password that isn't saved.
    private async System.Threading.Tasks.Task SwitchServerAsync(Guid server)
    {
        if (_history.Current is { } current)
            await OpenAsync(Workspace.OnServer(current, server), settingsMessage: null, _history.Visit, ActivitySources.ServerMenu);
    }

    // The page on screen and its server, opened again at the next start. Not in the activity log.
    // Settings isn't: it is visited, not worked in, and a first run starts there.
    private void RememberPage(Route shown)
    {
        if (shown.Tag == "Settings")
            return;
        try { SettingsService.SetLastPage(shown.Tag, Workspace.ServerOf(shown)); }
        catch (Exception ex) { AppLog.Error("Sidebar: could not save the page on screen", ex); }
    }

    private static string PasswordPrompt(ConnectionSettings conn) =>
        $"Enter the password for {conn.UserName} on {conn.Server} (\"{conn.DisplayName}\"), then click Save & make active.";

    private static string Fingerprint(ConnectionSettings? conn, int commandTimeoutSeconds) =>
        RepositoryFactory.Fingerprint(conn, commandTimeoutSeconds);

    // Builds the repository the pages use, from appsettings.json overlaid with the active connection.
    private IWaitStatsRepository BuildRepository(ConnectionSettings? active, int commandTimeoutSeconds)
    {
        _activeConnectionId = active?.Id;
        _activeFingerprint  = Fingerprint(active, commandTimeoutSeconds);

        var repo = RepositoryFactory.Create(active, commandTimeoutSeconds, out var resolvedConnectionString);
        _isConnectionConfigured = !string.IsNullOrWhiteSpace(resolvedConnectionString);
        return repo;
    }

    /// <summary>
    /// Re-reads the saved connections after Settings changed them. Swaps in a new repository
    /// when the active connection changed, refreshes the server header, and optionally opens the
    /// dashboard — so connection changes apply without restarting the app.
    /// </summary>
    internal async System.Threading.Tasks.Task ApplySavedConnectionsAsync(bool navigateToDashboard)
    {
        var store = SettingsService.Load();
        SyncConnections(store, ActivitySources.Settings);

        if (Fingerprint(store.Active, store.CommandTimeoutSeconds) != _activeFingerprint)
            await SwapRepositoryAsync(store);

        // A connection renamed or removed: the breadcrumb and Back and Forward follow (#139), and
        // the database the page reads (#140).
        UpdateHistoryButtons();
        ShowPageContext();

        // "Save & make active": the server's Overview (#143).
        if (navigateToDashboard)
            await NavigateTo("Overview");
    }

    private async System.Threading.Tasks.Task SwapRepositoryAsync(ConnectionStore store)
    {
        // Anything still loading was queried against the previous database.
        _loadVersion++;

        var previous = Repo;
        Repo = BuildRepository(store.Active, store.CommandTimeoutSeconds);

        // Best effort: a trace session started against the old connection shouldn't be
        // left running on that server.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await previous.StopTraceAsync(); } catch { /* old server may be unreachable */ }
        });

        await DisablePagesAzureSqlLacksAsync();

        // Back to a page this server doesn't have is skipped (#139).
        UpdateHistoryButtons();
    }

    // ── Saved servers ─────────────────────────────────────────────────────────

    // Brings monitoring, the server header and the breadcrumb's menu in line with the saved
    // connections. The source says why, for the activity log rows of any connection that starts or stops.
    private void SyncConnections(ConnectionStore store, string source)
    {
        Monitoring.Sync(store, source);
        RefreshServers(store);
    }

    private void RefreshServers(ConnectionStore store)
    {
        _selectorItems = store.Connections
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ConnectionSelectorItem(c))
            .ToList();
        foreach (var item in _selectorItems)
            item.UpdateHealth(Monitoring);
        ShowWorkspace();
    }

    private void OnAlertsChanged(Guid connectionId, IReadOnlyList<AlertChange> changes)
    {
        UpdateAlertsButton();

        // Only an alert starting or turning critical notifies; the settings are only read then.
        if (!changes.Any(c => c.Kind is AlertChangeKind.Started or AlertChangeKind.Escalated))
            return;

        var store = SettingsService.Load();
        if (!store.NotifiesFor(connectionId))
            return;

        var name = store.Connections.FirstOrDefault(c => c.Id == connectionId)?.DisplayName ?? "a deleted connection";
        if (AlertNotification.From(connectionId, name, changes) is { } notification)
            _tray.Notify(notification);
    }

    // Alerts with a count while any monitored connection has an active alert; the tray icon says the same.
    private void UpdateAlertsButton()
    {
        var active = Monitoring.ActiveAlerts;
        _tray.ShowActiveAlerts(active);
        Shell.SetCount("Alerts", active.Count, active.Count switch
        {
            0     => NavMenu.AlertsToolTip,
            1     => "1 active alert",
            var n => $"{n} active alerts",
        });
        // On ☰ while the sidebar is hidden.
        ShowSidebarState();
    }

    // Monitoring started or stopped somewhere: the tray says whether anything is monitored.
    private void OnMonitoringChanged()
    {
        _tray.ShowMonitoring(Monitoring.MonitoredCount);
        UpdateAlertsButton();
    }

    private void OnMonitoringStateChanged(Guid connectionId)
    {
        var item = _selectorItems.FirstOrDefault(i => i.Settings.Id == connectionId);
        if (item is null)
            return;

        item.UpdateHealth(Monitoring);
        // The server header (#141) and the breadcrumb's server part (#140) show its health and score.
        if (connectionId == WorkspaceServer)
            Shell.ShowServer(item);
        if (_history.Current is { } current && (connectionId == WorkspaceServer || connectionId == current.AlertsConnectionId))
            UpdateBreadcrumb();
        if (_scorePopupConnectionId == connectionId)
            ShowScoreBreakdown(item);
    }

    // ── Health score (#36) ────────────────────────────────────────────────────

    // The connection whose breakdown is open beside the sidebar; kept current as samples arrive.
    private Guid? _scorePopupConnectionId;

    // The score in the server header, or under its dot in the collapsed sidebar, opens its breakdown beside it.
    private void ShowWorkspaceScore(FrameworkElement anchor)
    {
        if (Item(WorkspaceServer) is not { } item)
            return;

        _scorePopupConnectionId    = item.Settings.Id;
        ShowScoreBreakdown(item);
        ScorePopup.PlacementTarget = anchor;
        ScorePopup.IsOpen          = true;
    }

    private void ShowScoreBreakdown(ConnectionSelectorItem item)
    {
        var session = Monitoring.Get(item.Settings.Id);
        ScoreBreakdown.Show(item.DisplayName, item.Score,
                            session is null ? Monitoring.NotMonitoredReason(item.Settings.Id) : session.HealthText,
                            paused: session is { IsRunning: false });
    }

    private void ScorePopup_Closed(object? sender, EventArgs e) => _scorePopupConnectionId = null;

    /// <summary>
    /// Makes a saved connection the active one: every server page then queries it. Opening one of
    /// a server's pages does this (#141).
    /// False when it couldn't: its password isn't saved (Settings is opened to ask for it), or the
    /// change couldn't be saved (the status bar says why).
    /// </summary>
    /// <param name="source">Where the user switched, for the activity log.</param>
    private async System.Threading.Tasks.Task<bool> SwitchToAsync(Guid connectionId, string source)
    {
        if (connectionId == _activeConnectionId)
            return true;

        var store  = SettingsService.Load();
        var target = store.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (target is null)
            return false;

        if (target.NeedsPassword)
        {
            // Switching would only fail the login: ask for the password first.
            await NavigateTo("Settings", PasswordPrompt(target), SettingsLocation.Connection(target.Id));
            return false;
        }

        try
        {
            SettingsService.SetActive(target.Id, source);
        }
        catch (Exception ex)
        {
            Bar.ShowMessage($"Could not switch: {ConnectionSettingsService.RedactSecrets(ex.Message)}", isError: true);
            return false;
        }

        store = SettingsService.Load();
        SyncConnections(store, source);
        await SwapRepositoryAsync(store);
        return true;
    }

    // Pages Azure SQL Database has nothing for (see the wiki's Server Impact and Permissions →
    // Compatibility), with the reason shown on their button.
    private static readonly (string Tag, string Reason)[] PagesAzureSqlLacks =
    [
        // TempDB file-level reporting relies on sys.master_files, which Azure SQL Database doesn't expose.
        ("TempDb",    "Not available on Azure SQL Database (sys.master_files is not accessible)"),
        // There is no SQL Server Agent, so msdb has no jobs (#39).
        ("AgentJobs", "Not available on Azure SQL Database, which has no SQL Server Agent. Elastic Jobs run scheduled work there."),
        // Azure takes the backups itself and records none in msdb (#40).
        ("Backups",   "Not available on Azure SQL Database, which backs up every database itself; the Azure portal shows its restore points."),
        // No sysadmin role, sa login or TRUSTWORTHY setting, and the logins are in the logical server's master (#43).
        ("SecurityAudit", "Not available on Azure SQL Database, which has no sysadmin role, sa login or TRUSTWORTHY setting; its logins are in the logical server's master database."),
    ];

    // Their buttons' own tooltips, to put back on a server that has them.
    private readonly Dictionary<string, object?> _ownToolTips = new();

    // The active connection is Azure SQL Database: checked after every switch.
    private bool _activeIsAzureSql;

    private bool IsDisabled(string tag) => _activeIsAzureSql && PagesAzureSqlLacks.Any(p => p.Tag == tag);

    // The sidebar shows them disabled while the open server is the active one. Another server's
    // edition is only known once it is switched to, by opening one of its pages; one it lacks then
    // opens its Overview instead (see OpenAsync).
    private void ApplyPagesAzureSqlLacks()
    {
        var known = WorkspaceServer is null || WorkspaceServer == _activeConnectionId;
        foreach (var (tag, reason) in PagesAzureSqlLacks)
        {
            var button = _sidebar.Button(tag);
            if (!_ownToolTips.ContainsKey(tag))
                _ownToolTips[tag] = button.ToolTip;
            var disabled = known && IsDisabled(tag);
            button.IsEnabled = !disabled;
            button.ToolTip   = disabled ? reason : _ownToolTips[tag];
            ToolTipService.SetShowOnDisabled(button, true);
        }
    }

    // Disable those pages up front instead of letting the user hit a query error after navigating to one.
    private async System.Threading.Tasks.Task DisablePagesAzureSqlLacksAsync()
    {
        _activeIsAzureSql = false;
        if (_isConnectionConfigured)
        {
            try { _activeIsAzureSql = await Repo.IsAzureSqlDatabaseAsync(); }
            catch { /* Edition check failed (e.g. server unreachable): leave the pages enabled. */ }
        }
        ApplyPagesAzureSqlLacks();
    }

    // ── Notification area (#35) ───────────────────────────────────────────────

    private bool KeepRunningInTray => SettingsService.Load().KeepRunningInTray;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState != WindowState.Minimized)
            _restoreState = WindowState;
        else if (KeepRunningInTray)
            HideToTray();
    }

    // Monitoring, history and alerts carry on; only the window goes. Windows it owns (a plan or
    // script window) hide and come back with it.
    private void HideToTray()
    {
        Hide();

        var store = SettingsService.Load();
        if (store.TrayHintShown)
            return;

        _tray.Inform("SqlVitals is still running",
            (Monitoring.MonitoredCount > 0
                ? "Monitoring carries on and alerts still notify you. "
                : "Nothing is being monitored; start it from the icon's menu. ") +
            "Click the SqlVitals icon here to open it, or right-click it and choose Exit to quit.");
        try { SettingsService.SetTrayHintShown(); }
        catch (Exception ex) { AppLog.Error("Tray: could not save that the hint was shown", ex); }
    }

    /// <summary>Brings the window back from the tray (or from behind other windows) as it was.</summary>
    internal void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Show();
        Activate();
    }

    // Raised on a SystemEvents thread; OnResumed is thread-safe.
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            Chat.OnResumed();
    }

    // Once each time a chat channel starts failing, even while the window is hidden, and whatever
    // connections are muted: it's about the channel (#122).
    private void OnChatChannelFailing(Guid channelId, string text) =>
        _tray.Inform(ChannelStatus.FailingTitle, text, warning: true);

    // Exit in the tray menu: quit for real, with the usual clean-up in OnClosing.
    private void ExitApp()
    {
        ((App)Application.Current).IsExiting = true;
        Close();
    }

    // Last line of defence for the live SP trace: SpTracePage drops its event session when
    // the user navigates away, but closing the window while that page is open does not
    // raise Unloaded reliably. Without this the session keeps collecting on the monitored
    // server after the app is gone.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window only hides it, unless the user asked to exit or Windows is ending
        // the session.
        if (!((App)Application.Current).IsExiting && KeepRunningInTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // A status report still queued for the UI thread mustn't reach the disposed icon.
        Chat.StartedFailing -= OnChatChannelFailing;
        _tray.Dispose();
        // A static event: it would keep this window alive.
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        // Background collectors hold no server-side state; just stop them polling. Their alerts'
        // ends reach the chat channels as one "stopped monitoring" message each, which send while
        // the history is flushed; then whatever is still waiting gets the rest of a few seconds
        // (#123). Closing to the notification area doesn't come here.
        var exitStarted = System.Diagnostics.Stopwatch.StartNew();
        Chat.BeginExit();
        Monitoring.Dispose();
        Chat.Shutdown(ChatNotificationService.ExitTime - exitStarted.Elapsed);
        Chat.Dispose();

        try
        {
            // Stop the page's poll timer first. Each poll holds the trace repository's gate
            // for a full server round trip, so leaving it running eats the budget below and
            // the session survives the app — which is what it did before this call existed.
            if (MainFrame.Content is SpTracePage tracePage)
                tracePage.PrepareForShutdown();

            // Task.Run keeps the await continuations off the UI thread — blocking on them
            // here with a live SynchronizationContext would deadlock. The budget covers a
            // cold connection to a remote server (Azure SQL routinely needs several
            // seconds) plus the DROP itself.
            System.Threading.Tasks.Task.Run(() => Repo.StopTraceAsync())
                .Wait(TimeSpan.FromSeconds(20));
        }
        catch
        {
            // Shutting down regardless. A session left behind is dropped by name on the
            // next Start Trace.
        }

        base.OnClosing(e);
    }

    // Returns a compact one-line status bar string, e.g. "Error [WaitStatsRepository.GetCumulativeWaitsAsync]: Timeout"
    internal static string FormatErrorStatus(Exception ex) => ErrorStatus.Format(ex);

    // Timer-driven page refreshes report here rather than through a MessageBox, which would
    // pop up again on every tick while the server stays unreachable. The details go to the
    // log; the top bar shows the one-line error (#140). A page the user has navigated away from
    // no longer owns the bar, so only its log entry is kept.
    internal static void ReportBackgroundError(Page page, string source, Exception ex)
    {
        AppLog.Error($"{source}: collection failed", ex);
        if (OwnerOf(page) is { } main)
            main.Bar.ShowMessage(FormatErrorStatus(ex), isError: true);
    }

    // The page updated on its own: a live page's "As of", a snapshot's "Taken" (#140). Replaces an
    // earlier background error once the page collects successfully again.
    internal static void ReportBackgroundSuccess(Page page)
    {
        if (OwnerOf(page) is not { } main)
            return;
        main._updatedAt = DateTime.Now;
        if (main.Bar.MessageIsError)
            main.Bar.ShowMessage(null);
        main.ShowPageContext();
    }

    /// <summary>
    /// A page's <see cref="IPageContext"/> changed: it paused or resumed, picked another time
    /// range, or read a new window. The top bar follows (#140).
    /// </summary>
    internal static void ReportContextChanged(Page page) => OwnerOf(page)?.ShowPageContext();

    // The window showing this page, while it is the page on screen.
    private static MainWindow? OwnerOf(Page page) =>
        Window.GetWindow(page) is MainWindow main && ReferenceEquals(main._page, page) ? main
        : Application.Current?.MainWindow is MainWindow app && ReferenceEquals(app._page, page) ? app
        : null;

    // Returns the full message for the MessageBox, with the searchable tag on its own line.
    private static string FormatErrorDetail(Exception ex)
    {
        if (ex is WaitStatsException wse)
        {
            var sqlNote = wse.SqlErrorNumber != 0 ? $"\nSQL error number: {wse.SqlErrorNumber}" : string.Empty;
            return ConnectionSettingsService.RedactSecrets(
                $"Error tag:  {wse.ErrorTag}{sqlNote}\n\n{ex.InnerException?.Message ?? ex.Message}\n\nSearch the codebase for \"{wse.ErrorTag}\" to locate the originating query.");
        }
        return ConnectionSettingsService.RedactSecrets(ex.Message);
    }

    // ── Home and a server's Overview (#130, #143) ─────────────────────────────

    // When each server's Overview was last opened, for its "Since you last looked".
    private readonly OverviewVisits _overviewVisits;

    private HomePage CreateHomePage()
    {
        var page = new HomePage(Monitoring, SettingsService);
        page.OpenRequested          += async id => await NavigateTo("Overview", serverId: id);
        page.InvestigateRequested   += async alert => await OpenEvidenceAsync(alert, ActivitySources.Home);
        page.AddConnectionRequested += async () => await NavigateTo("Settings", settingsLocation: SettingsLocation.NewConnectionPage);
        page.EditRequested          += async id => await NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id));
        return page;
    }

    private OverviewPage CreateOverviewPage(Guid connectionId)
    {
        var page = new OverviewPage(connectionId, Monitoring, SettingsService, _overviewVisits.Open(connectionId));
        page.EditRequested       += async id => await NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id));
        page.ThresholdsRequested += async id => await NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id, ConnectionTab.Thresholds));
        page.AlertsRequested     += async id => await NavigateTo("Alerts", alertsConnectionId: id);
        page.MakeActiveRequested += async id =>
        {
            if (await SwitchToAsync(id, ActivitySources.Overview))
                Bar.ShowMessage($"Switched to \"{SettingsService.Load().Connections.FirstOrDefault(c => c.Id == id)?.DisplayName}\".");
        };
        // A page about the server: Deadlocks, Live charts, the rest of "Since you last looked"… It
        // queries the active connection, so opening it makes this one active (see OpenAsync).
        page.OpenPageRequested += async (id, tag) => await NavigateTo(tag, serverId: id, switchSource: ActivitySources.Overview);
        // An alert's or a check's evidence, filtered and set to its time (#145).
        page.EvidenceRequested += async route => await OpenAsync(route, settingsMessage: null, _history.Visit, ActivitySources.Overview);
        return page;
    }

    // Investigate goes through the navigator (#182), to OpenEvidenceAsync.
    private AlertsPage CreateAlertsPage(Guid? connectionId) => new(Monitoring, SettingsService, connectionId);

    // ── An alert's evidence (#145) ────────────────────────────────────────────

    /// <summary>
    /// Opens an alert's evidence: its page on its server, filtered to the problem and set to its
    /// time, through the navigator so Back returns. The alert as it stands now: one a notification
    /// was about may have ended since. A deleted connection's alert has no server: Alerts instead.
    /// </summary>
    internal async System.Threading.Tasks.Task OpenEvidenceAsync(Alert alert, string source)
    {
        if (Item(alert.ConnectionId) is null)
        {
            await NavigateTo("Alerts");
            Bar.ShowMessage("That alert's connection has been removed, so there is no server to look at.");
            return;
        }
        await OpenAsync(EvidenceRoutes.ForAlert(await LatestAsync(alert)), settingsMessage: null, _history.Visit, source);
    }

    private async System.Threading.Tasks.Task<Alert> LatestAsync(Alert alert)
    {
        if (Monitoring.ActiveAlerts.FirstOrDefault(a => a.Id == alert.Id) is { } active)
            return active;
        if (!alert.IsActive)
            return alert;

        // Not active any more: the history has its end.
        try
        {
            var reader = Monitoring.HistoryReader;
            var stored = await System.Threading.Tasks.Task.Run(() =>
                reader.ReadAlerts(alert.ConnectionId, alert.StartedUtc.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1)));
            return stored.Select(s => s.Alert).FirstOrDefault(a => a.Id == alert.Id) ?? alert;
        }
        catch (Exception ex)
        {
            AppLog.Error("Evidence: could not read the alert's end from the history", ex);
            return alert;
        }
    }

    // Show all: the page clears its filter where it is, and the history keeps it unfiltered, so
    // Back and Forward return to it as it is now.
    private void ShowAllEvidence()
    {
        if (_history.Current is not { Focus: not null } current)
            return;
        if (_page is IFocusPage page)
        {
            try { page.ClearFocus(); }
            catch (Exception ex) { AppLog.Error($"{current.Tag}: could not clear its filter", ex); }
        }
        _history.ReplaceCurrent(EvidenceRoutes.Unfocused(current));
        UpdateHistoryButtons();
        ShowFocusBanner();
    }

    // "Opened from alert" over a page opened as evidence; nothing over any other.
    private void ShowFocusBanner()
    {
        var focus = _history.Current?.Focus;
        var page  = _page as IFocusPage;
        var text  = focus is null ? null
            : FocusBanner.For(focus, page?.FocusNote, page?.ShowsWindow == true, PageName(focus.HistoryTag), DateTime.Today);
        FocusBar.Show(text, page?.ShowAllText);
    }

    // The server's clock from UTC, from its latest sample; null when it isn't being sampled (see ServerClock).
    private TimeSpan? ServerOffset(Guid? connectionId) =>
        connectionId is { } id && Monitoring.Get(id) is { Samples.Count: > 0, LastSuccess: { } last } session
            ? SqlVitals.Engine.Home.ServerClock.Offset(session.Samples[^1].Time, last.ToUniversalTime())
            : null;

    private static string? PageName(string? tag) =>
        tag is null ? null : NavMenu.AllPages.FirstOrDefault(p => p.Tag == tag)?.Label;

    /// <summary>
    /// Opens another page of the same server with a filter, for links between pages that carry it
    /// (#145): a session from Processes on Active Waits, a query on Query Store.
    /// </summary>
    /// <param name="fromPage">The page the link is on, as its banner names it.</param>
    internal System.Threading.Tasks.Task OpenLinkAsync(string tag, FocusFilter filter, string fromPage) =>
        OpenAsync(EvidenceRoutes.Link(tag, _history.Current?.ConnectionId ?? _activeConnectionId, filter, fromPage),
                  settingsMessage: null, _history.Visit, ActivitySources.Page(fromPage));

    // Settings (#131): links out of it come back here, and switching theme opens it again.
    private SettingsPage CreateSettingsPage(SettingsLocation? location, string? message)
    {
        var page = new SettingsPage(SettingsService, Monitoring, Chat, ApplySavedConnectionsAsync,
                                    location ?? new SettingsLocation(_settingsCategory), message);
        page.CategoryShown   += category => _settingsCategory = category;
        page.ServerRequested += async id => await NavigateTo("Overview", serverId: id);
        page.ThemeChanged    += async () => await NavigateTo("Settings", settingsLocation: new SettingsLocation(SettingsCategory.Appearance));
        return page;
    }

    /// <summary>Opens another page, as its sidebar button would; for links between pages (<see cref="INavigator"/>).</summary>
    /// <param name="serverId">The server a page is about; null for the active connection.</param>
    internal System.Threading.Tasks.Task OpenPageAsync(string tag, Guid? serverId = null) => NavigateTo(tag, serverId: serverId);

    // ── Quick switcher (#144) ─────────────────────────────────────────────────

    // Over everything else: a menu the sidebar has open closes first.
    private void OpenSwitcher()
    {
        Shell.CloseFlyout(focusIcon: false);
        Shell.CloseSlideOver(returnFocus: false);
        ScorePopup.IsOpen = false;

        var scope = SwitcherScope();
        Switcher.Open(text => QuickSwitcher.Search(text, scope), id => Item(id),
                      keepsPage: WorkspaceServer is not null && _selectorItems.Count > 1);
    }

    // What the switcher searches: the open server's pages (the active one's on a page about every
    // server, as the sidebar would open them), the saved servers, and the recent routes still available.
    private SwitcherScope SwitcherScope()
    {
        var isAvailable = Availability();
        return new SwitcherScope(
            _history.Current,
            WorkspaceServer ?? _activeConnectionId,
            _selectorItems.Select(i => new SwitcherServer(i.Settings.Id, i.DisplayName, i.Server)).ToList(),
            _recent.Items.Where(isAvailable).ToList())
        {
            // Only the active server's edition is known; another's pages it lacks open its Overview.
            Unavailable = (tag, server) =>
                server == _activeConnectionId && IsDisabled(tag) ? PagesAzureSqlLacks.First(p => p.Tag == tag).Reason : null,
        };
    }

    // ── Top bar (#140) ────────────────────────────────────────────────────────

    // Refresh in the top bar, or F5: the page on screen reads again.
    private async System.Threading.Tasks.Task RefreshPageAsync()
    {
        if (_page is not IRefreshable page || !Bar.CanRefresh)
            return;

        var version = _loadVersion;
        Bar.CanRefresh = false;
        Bar.ShowMessage("Refreshing…");
        try
        {
            await page.RefreshAsync();
            if (version == _loadVersion)
            {
                _updatedAt = DateTime.Now;
                Bar.ShowMessage(null);
                ShowPageContext();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"{_history.Current?.Tag}: refresh failed", ex);
            if (version == _loadVersion)
                Bar.ShowMessage(FormatErrorStatus(ex), isError: true);
        }
        finally
        {
            if (version == _loadVersion)
                Bar.CanRefresh = true;
        }
    }

    // Pause or Resume in the top bar, for a live page that can.
    private void PausePage(bool pause)
    {
        if (_page is not IPageContext { CanPause: true } context)
            return;
        try
        {
            context.SetPaused(pause);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{_history.Current?.Tag}: could not {(pause ? "pause" : "resume")}", ex);
        }
        ShowPageContext();
    }

    // What the top bar says about the page on screen: its kind and freshness, Pause, and the
    // database it reads. Nothing before the first page.
    private void ShowPageContext()
    {
        if (_page is not IPageContext context)
        {
            Bar.ShowContext(null, canPause: false, database: null);
            ShowFocusBanner();
            return;
        }
        Bar.ShowContext(Freshness(context), context.CanPause, DatabaseOf(context));
        // What a page opened as evidence shows changes as it reads (#145).
        ShowFocusBanner();
    }

    private PageFreshness Freshness(IPageContext page) => page.Kind switch
    {
        PageKind.Live     => new(PageKind.Live, page.LiveInterval, page.IsPaused, AsOf: _updatedAt),
        PageKind.Snapshot => new(PageKind.Snapshot, Taken: _updatedAt),
        PageKind.History  => new(PageKind.History, Window: page.HistoryWindow, CompareWith: page.CompareWith, DataTo: page.DataTo),
        _                 => new(PageKind.None),
    };

    // The active connection's database, as Settings has it, on a page that reads it; null
    // for a connection from appsettings.json only, whose database isn't known here.
    private string? DatabaseOf(IPageContext page) =>
        page.UsesDatabase ? _selectorItems.FirstOrDefault(i => i.Settings.Id == _activeConnectionId)?.DatabaseLabel : null;

    // ── Navigation (#139) ─────────────────────────────────────────────────────

    /// <summary>Opens a page as a new visit: the sidebar, a link, the tray, a Home tile.</summary>
    /// <param name="settingsMessage">Shown at the top of Settings.</param>
    /// <param name="settingsLocation">Where Settings opens (#131); the category last used when null.</param>
    /// <param name="serverId">The server a page is about (#141): an Overview's, or the connection a
    /// server's page queries; null for the active connection.</param>
    /// <param name="switchSource">Why, in the activity log, if opening it switches the active connection.</param>
    private System.Threading.Tasks.Task NavigateTo(string tag, string? settingsMessage = null, SettingsLocation? settingsLocation = null,
                                                   Guid? alertsConnectionId = null, Guid? serverId = null,
                                                   string switchSource = ActivitySources.Sidebar) =>
        OpenAsync(RouteFor(tag, settingsLocation, alertsConnectionId, serverId), settingsMessage, _history.Visit, switchSource);

    // A server's page queries the server it's opened on, the active connection by default; a page
    // about every connection, or one by id, queries none.
    private Route RouteFor(string tag, SettingsLocation? settingsLocation = null, Guid? alertsConnectionId = null, Guid? serverId = null) =>
        new(tag,
            ConnectionId:       Route.IsGlobal(tag) ? null : serverId ?? _activeConnectionId,
            ServerId:           tag == "Overview" ? serverId ?? _activeConnectionId : null,
            AlertsConnectionId: tag == "Alerts" ? alertsConnectionId : null,
            Settings:           tag == "Settings" ? settingsLocation : null);

    // Alt+← / Alt+→ and the keyboard's own Back and Forward keys, before the Frame's built-in
    // journal commands see them (its journal is kept empty). F5 refreshes the page (#140), rather
    // than the Frame reloading it. Ctrl+B pins, collapses or hides the sidebar (#142). Ctrl+K opens
    // or closes the quick switcher (#144); while it is open, the other keys are its own.
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (Switcher.IsOpen)
                Switcher.Close(restoreFocus: true);
            else
                OpenSwitcher();
            return;
        }
        if (Switcher.IsOpen)
            return;

        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await RefreshPageAsync();
            return;
        }

        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Shell.Cycle();
            return;
        }

        // F11: focus mode on or off (#148).
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            ToggleFocus();
            return;
        }

        // Ctrl+J opens or closes the details drawer (#147).
        if (e.Key == Key.J && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Details.Toggle();
            return;
        }

        bool? back = e.Key switch
        {
            Key.BrowserBack    => true,
            Key.BrowserForward => false,
            Key.System when Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left  => true,
            Key.System when Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Right => false,
            _ => null,
        };
        if (back is null)
            return;
        e.Handled = true;
        await GoAsync(back.Value);
    }

    // Esc closes a floating details drawer (#147), once nothing in the page has used it: a filter
    // box clearing its text, a grid cancelling an edit, a pop-up closing come first.
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None || Switcher.IsOpen)
            return;
        // A floating drawer first, then focus mode (#148).
        if (Details.Escape())
            e.Handled = true;
        else if (InFocus)
        {
            ExitFocus();
            e.Handled = true;
        }
    }

    // The mouse's back and forward buttons.
    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2) || Switcher.IsOpen)
            return;
        e.Handled = true;
        await GoAsync(back: e.ChangedButton == MouseButton.XButton1);
    }

    /// <summary>
    /// Back or Forward to the nearest route still available, switching to its connection first
    /// when it's another server's page. Routes skipped on the way are dropped from the history.
    /// </summary>
    internal async System.Threading.Tasks.Task GoAsync(bool back)
    {
        if (_historyMovePending)
            return;

        var isAvailable = Availability();
        var target = back ? _history.PeekBack(isAvailable) : _history.PeekForward(isAvailable);
        if (target is null)
            return;

        _historyMovePending = true;
        try
        {
            await OpenAsync(target, settingsMessage: null, shown =>
            {
                if (back) _history.CommitBack(target, shown);
                else      _history.CommitForward(target, shown);
                _historyMovePending = false;
            });
        }
        finally
        {
            // Also when the move was refused: Settings kept the user, or the switch failed.
            _historyMovePending = false;
            UpdateHistoryButtons();
        }
    }

    /// <summary>
    /// Shows a route: asks Settings first if it has unsaved changes, switches to the route's
    /// connection if it's another one's, builds the page, records the move with
    /// <paramref name="record"/>, loads it, and puts back how it was being looked at.
    /// </summary>
    /// <param name="record">Records the route actually shown in the history, before the page loads.</param>
    /// <param name="switchSource">Why, in the activity log, if the route's connection becomes the active one.</param>
    private async System.Threading.Tasks.Task OpenAsync(Route route, string? settingsMessage, Action<Route> record,
                                                        string switchSource = ActivitySources.BackOrForward)
    {
        // A connection's page, or the default thresholds, may hold unsaved changes: ask first, and
        // stay when the user says so (#131). The page navigated to, as the Frame shows it a moment later.
        if (_page is SettingsPage leaving && !await leaving.TryLeaveAsync())
            return;

        // A notification opening a page while the quick switcher is open: the page it was opened over goes.
        Switcher.Close(restoreFocus: false);

        SaveViewState();

        // Another server's page (from its workspace, the server menu, or Back): that server becomes
        // the active one. Without its saved password, Settings opens to ask for it.
        if (route.ConnectionId is { } connectionId && connectionId != _activeConnectionId &&
            !await SwitchToAsync(connectionId, switchSource))
            return;

        var version = ++_loadVersion;
        var tag = route.Tag;
        var settingsLocation = route.Settings;

        // An Overview needs its server; without one, Home lists them all.
        if (tag == "Overview" && route.ServerId is null)
            tag = "Home";

        // Without a connection every data page would just fail — point the user at Settings.
        // Alerts only reads the local history, and a notification can lead there with none active.
        // Home and a server's details only read the sessions and the history too (#130).
        if (!_isConnectionConfigured && !Route.IsGlobal(tag))
        {
            tag = "Settings";
            settingsMessage ??= SettingsService.Load().Connections.Count == 0
                ? "No SQL Server connection is configured yet. Click + New connection to add one, then Save & make active."
                : "No server is open. Open one from All servers, or add a new connection.";
            settingsLocation ??= new SettingsLocation(SettingsCategory.Connections);
        }

        // A page this server doesn't have, e.g. Agent Jobs on an Azure SQL Database just switched
        // to: its Overview instead (#141).
        var shown = Workspace.Available(tag == route.Tag ? route : RouteFor(tag, settingsLocation), IsDisabled, _activeConnectionId);
        tag = shown.Tag;
        record(shown);
        _recent.Add(shown);
        UpdateHistoryButtons();
        ShowWorkspace();
        RememberPage(shown);

        // Highlight the page's button, and open its group.
        System.Diagnostics.Debug.Assert(_sidebar.HasPage(tag), $"No sidebar button for \"{tag}\": add it to NavMenu.");
        Shell.Highlight(tag);

        // A banner hidden on the page left shows again on the next page opened as evidence.
        FocusBar.NewPage();

        // Settings page does not implement IRefreshable — handle separately
        if (tag == "Settings")
        {
            var settings = CreateSettingsPage(settingsLocation, settingsMessage);
            MainFrame.Navigate(settings);
            _page      = settings;
            _updatedAt = null;
            ShowFocusBanner();
            ShowDetailsOf(settings);
            Bar.ShowPage(tools: null, Freshness(settings), canPause: false, database: null);
            Bar.CanRefresh = false;
            if (!_isConnectionConfigured)
                Bar.ShowMessage("Not connected");
            return;
        }

        var activeName = _selectorItems.FirstOrDefault(i => i.Settings.Id == _activeConnectionId)?.DisplayName;
        IRefreshable page = tag switch
        {
            "Home"            => CreateHomePage(),
            "Overview"        => CreateOverviewPage(shown.ServerId!.Value),
            "LiveMetrics"     => new LiveMetricsDashboardPage(Repo, Monitoring, _activeConnectionId, Monitoring.HistoryFor(_activeConnectionId), activeName),
            "Alerts"          => CreateAlertsPage(shown.AlertsConnectionId),
            "TopWaits"        => new TopWaitsPage(Repo, SettingsService),
            "ActiveWaits"     => new ActiveWaitsPage(Repo, SettingsService),
            "Processes"       => new ProcessesPage(Repo, SettingsService),
            "Deadlocks"       => new DeadlocksPage(Repo, SettingsService),
            "AgentJobs"       => new AgentJobsPage(Repo, SettingsService),
            "Backups"         => new BackupsPage(Repo, SettingsService),
            "FileIo"          => new FileIoPage(Repo, SettingsService),
            "ConfigChecks"    => new ConfigChecksPage(Repo, activeName, SettingsService),
            "SecurityAudit"   => new SecurityAuditPage(Repo, activeName, SettingsService),
            "WaitTrend"       => new WaitStatsTrendPage(Repo, Monitoring.HistoryFor(_activeConnectionId), SettingsService),
            "TempDb"          => new TempDbPage(Repo, SettingsService),
            "Memory"          => new MemoryGrantsPage(Repo, SettingsService),
            "QueryStore"      => new QueryStorePage(Repo, SettingsService),
            "QueryRegressions" => new QueryRegressionsPage(Repo, Monitoring.HistoryFor(_activeConnectionId), SettingsService),
            "IndexHealth"     => new IndexHealthPage(Repo, SettingsService),
            "ResourceQueries" => new ResourceQueriesPage(Repo, ServerOffset(_activeConnectionId), SettingsService),
            "ImplicitConv"    => new ImplicitConversionsPage(Repo, SettingsService),
            "PlanCacheHealth" => new PlanCacheHealthPage(Repo, SettingsService),
            "StaleStats"      => new StaleStatisticsPage(Repo, SettingsService),
            "DbStorage"       => new DatabaseStoragePage(Repo, SettingsService),
            "AppConnections" => new ApplicationConnectionsPage(Repo, SettingsService),
            "Perfmon"        => new PerfmonPage(Repo, Monitoring.HistoryFor(_activeConnectionId), SettingsService),
            "SpTrace"         => new SpTracePage(Repo, SettingsService),
            "Export"          => new ExportPage(Repo, SettingsService),
            _                 => new LiveMetricsDashboardPage(Repo, Monitoring, _activeConnectionId, Monitoring.HistoryFor(_activeConnectionId), activeName),
        };

        System.Diagnostics.Debug.Assert(PageCatalog.Types.GetValueOrDefault(tag) == page.GetType() || !PageCatalog.Types.ContainsKey(tag),
                                        $"PageCatalog says \"{tag}\" opens another type than {page.GetType().Name}.");

        // The page's own controls (a time range picker) go into the top bar before it loads, with
        // its kind; the previous page's go (#140).
        var context = (IPageContext)page;
        FrameworkElement? tools = null;
        try { tools = context.TakeBarTools(); }
        catch (Exception ex) { AppLog.Error($"{tag}: could not move its controls to the top bar", ex); }

        // Opened as evidence (#145): filtered to the problem, or set to the alert's window, from its
        // first load on.
        if (shown.Focus is { } focus && page is IFocusPage focusPage)
        {
            try { focusPage.ShowFocus(focus); }
            catch (Exception ex) { AppLog.Error($"{tag}: could not apply its filter", ex); }
        }

        MainFrame.Navigate(page);
        _page       = page as Page;
        _pageLoaded = false;
        _updatedAt  = null;
        ShowDetailsOf(page);
        Bar.ShowPage(tools, Freshness(context), context.CanPause, DatabaseOf(context));
        ShowFocusBanner();

        // How the page was left last time: its tab now, each grid's sort, filter and selection
        // as its rows arrive, so the page loads straight into it (#139).
        var viewState = _viewStates.Get(shown);
        if (viewState is not null && page is Page opened)
            RestoreViewState(() => viewState.Restore(opened));

        Bar.CanRefresh = false;
        Bar.ShowMessage("Loading…");
        try
        {
            await page.RefreshAsync();
            if (version == _loadVersion)
            {
                _updatedAt = DateTime.Now;
                Bar.ShowMessage(null);
                ShowPageContext();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"{tag}: load failed", ex);

            // The user has already moved on (another page or another database) — an error
            // from this load would describe data that is no longer on screen.
            if (version != _loadVersion)
                return;

            Bar.ShowMessage(FormatErrorStatus(ex), isError: true);
            _dialogs.Warn(
                FormatErrorDetail(ex) + "\n\nIf the server details are wrong, update the connection in Settings — no restart needed.",
                "Data Error");
        }
        finally
        {
            if (version == _loadVersion)
            {
                Bar.CanRefresh = true;

                // Loaded or not, the page is as long as it will be: scroll it back.
                _pageLoaded = true;
                if (viewState is not null && page is Page loaded)
                    RestoreViewState(() => viewState.RestoreScrolling(loaded));
            }
        }
    }

    // Keeps how the page on screen is being looked at, for when its route is opened again.
    // Settings keeps its own place (#131); a page left before it loaded keeps what it had.
    private void SaveViewState()
    {
        if (_history.Current is not { } current || _page is null or SettingsPage || !_pageLoaded)
            return;

        try
        {
            _viewStates.Save(current, ViewState.Capture(_page, _viewStates.Get(current)));
        }
        catch (Exception ex)
        {
            // Losing a page's sort or scroll position isn't worth stopping the navigation for.
            AppLog.Error($"{current.Tag}: could not keep the view state", ex);
        }
    }

    private void RestoreViewState(Action restore)
    {
        try { restore(); }
        catch (Exception ex) { AppLog.Error($"{_history.Current?.Tag}: could not put back the view state", ex); }
    }

    // Which routes Back and Forward can still open: no connection they name has been removed,
    // and the active server has the page. Another server's Azure pages are only known once
    // switched to, and then its Overview opens instead. The saved connections are the ones held here,
    // kept current as they change, rather than the settings file read again.
    private Func<Route, bool> Availability()
    {
        var ids = _selectorItems.Select(i => i.Settings.Id).ToHashSet();
        bool Exists(Guid? id) => id is not { } value || ids.Contains(value);
        return route => Exists(route.ConnectionId) && Exists(route.ServerId) && Exists(route.AlertsConnectionId)
                     && Exists(route.Settings?.ConnectionId)
                     && !(route.ConnectionId == _activeConnectionId && IsDisabled(route.Tag));
    }

    // Back and Forward say where they go, and the breadcrumb says where you are (#140).
    private void UpdateHistoryButtons()
    {
        var isAvailable = Availability();
        var back    = _history.PeekBack(isAvailable);
        var forward = _history.PeekForward(isAvailable);
        Bar.ShowHistory(back is null ? null : Describe(back), forward is null ? null : Describe(forward));
        UpdateBreadcrumb();
    }

    // All servers › PROD-SQL01 › Activity now › Processes, with the server's health.
    private void UpdateBreadcrumb()
    {
        if (_history.Current is not { } current)
            return;
        Bar.ShowBreadcrumb(Breadcrumb.For(current, id => Item(id)?.DisplayName), id => Item(id), _selectorItems);
    }

    // "Processes · PROD-SQL01", "Overview · PROD-SQL01", "Alerts" for a page about every server.
    private string Describe(Route route) => Breadcrumb.Describe(route, id => Item(id)?.DisplayName);
}
