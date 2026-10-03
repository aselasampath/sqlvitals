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

    // Set while the selector is repopulated in code, so it doesn't trigger a switch.
    private bool _isUpdatingSelector;

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
        AppVersionText.Text = $"v{version?.Major}.{version?.Minor}.{version?.Build}";

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
        Bar.DatabaseRequested += async () =>
        {
            if (_activeConnectionId is { } id)
                await NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id));
        };

        SettingsService = new ConnectionSettingsService();
        var store  = SettingsService.Load();
        var active = store.Active;
        BuildSidebar(store.NavCollapsedGroups);
        Repo = BuildRepository(active, store.CommandTimeoutSeconds);

        _tray = new TrayIcon(SettingsService);
        _tray.OpenRequested   += ShowFromTray;
        _tray.AlertsRequested += async connectionId =>
        {
            ShowFromTray();
            await NavigateTo("Alerts", alertsConnectionId: connectionId);
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
                    : "No connection is active. Choose a saved connection in the sidebar selector, or add a new one.",
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
            // With more than one server, Home shows them all at a glance (#130).
            await NavigateTo(store.Connections.Count >= 2 ? "Home" : "LiveMetrics");
        };
    }

    // ── Sidebar (#124) ────────────────────────────────────────────────────────

    // Built from NavMenu in the constructor, before anything can update a button.
    private NavSidebar _sidebar = null!;

    private Button NavButton(string tag) => _sidebar.Button(tag);

    private void BuildSidebar(IReadOnlyList<string> collapsed)
    {
        _sidebar = new NavSidebar(NavPanel, collapsed);
        _sidebar.PageRequested += async tag => await NavigateTo(tag);

        // Remembered for the next run, but not in the activity log. Opening a page in a collapsed
        // group expands it, and that is saved too, so the sidebar comes back as it was left.
        _sidebar.GroupToggled += (id, isCollapsed) =>
        {
            try { SettingsService.SetNavGroupCollapsed(id, isCollapsed); }
            catch (Exception ex) { AppLog.Error("Sidebar: could not save which groups are collapsed", ex); }
        };
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
    /// when the active connection changed, refreshes the selector, and optionally opens the
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

        if (navigateToDashboard)
            await NavigateTo("LiveMetrics");
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

        foreach (var page in PagesAzureSqlLacks)
        {
            page.Button.IsEnabled = true;
            page.Button.ToolTip   = page.ToolTip;
        }
        await DisablePagesAzureSqlLacksAsync();

        // Back to a page this server doesn't have is skipped (#139).
        UpdateHistoryButtons();
    }

    // ── Connection selector ───────────────────────────────────────────────────

    // Brings monitoring and the selector in line with the saved connections. The source says
    // why, for the activity log rows of any connection that starts or stops.
    private void SyncConnections(ConnectionStore store, string source)
    {
        Monitoring.Sync(store, source);
        RefreshConnectionSelector(store);
    }

    private void RefreshConnectionSelector(ConnectionStore store)
    {
        _isUpdatingSelector = true;
        try
        {
            _selectorItems = store.Connections
                .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new ConnectionSelectorItem(c))
                .ToList();
            foreach (var item in _selectorItems)
                item.UpdateHealth(Monitoring);

            var activeItem = _selectorItems.FirstOrDefault(i => i.Settings.Id == store.ActiveConnectionId);
            CmbConnection.ItemsSource  = _selectorItems;
            CmbConnection.SelectedItem = activeItem;
            CmbConnection.IsEnabled    = _selectorItems.Count > 0;
            CmbConnection.ToolTip      = activeItem?.ToolTipText;

            TxtNoActiveConnection.Text = _selectorItems.Count == 0
                ? "No saved connections. Click Manage to add one."
                : "No active connection. Choose one above.";
            TxtNoActiveConnection.Visibility = store.Active is null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isUpdatingSelector = false;
        }
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

    // "🔔  Alerts (2)" while any monitored connection has an active alert; the tray icon says the same.
    private void UpdateAlertsButton()
    {
        var active = Monitoring.ActiveAlerts;
        _tray.ShowActiveAlerts(active);
        var alerts = NavButton("Alerts");
        alerts.Content = active.Count == 0 ? "🔔  Alerts" : $"🔔  Alerts ({active.Count})";
        System.Windows.Automation.AutomationProperties.SetName(alerts,
            active.Count == 0 ? "Alerts" : $"Alerts ({active.Count} active)");
        alerts.ToolTip = active.Count switch
        {
            0     => NavMenu.AlertsToolTip,
            1     => "1 active alert",
            var n => $"{n} active alerts",
        };
    }

    // Monitoring started or stopped somewhere: the tray says whether anything is monitored.
    private void OnMonitoringChanged()
    {
        _tray.ShowMonitoring(Monitoring.MonitoredCount);
        UpdateAlertsButton();
    }

    // ▶ / ■ in a selector row (#115): starts or stops that connection without switching to it.
    // The button takes the click, so the row isn't selected.
    private void MonitorToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConnectionSelectorItem item })
            return;

        e.Handled = true;
        if (Monitoring.IsMonitoring(item.Settings.Id))
            Monitoring.Stop(item.Settings.Id, ActivitySources.ConnectionSelector);
        else
            Monitoring.Start(item.Settings.Id, ActivitySources.ConnectionSelector);
    }

    private void OnMonitoringStateChanged(Guid connectionId)
    {
        var item = _selectorItems.FirstOrDefault(i => i.Settings.Id == connectionId);
        if (item is null)
            return;

        item.UpdateHealth(Monitoring);
        if (ReferenceEquals(CmbConnection.SelectedItem, item))
            CmbConnection.ToolTip = item.ToolTipText;
        // The breadcrumb's server part shows its health dot and score (#140).
        if (_history.Current is { } current && connectionId == (current.ConnectionId ?? current.ServerId ?? current.AlertsConnectionId))
            UpdateBreadcrumb();
        if (_scorePopupConnectionId == connectionId)
            ShowScoreBreakdown(item);
    }

    // ── Health score (#36) ────────────────────────────────────────────────────

    // The connection whose breakdown is open beside the sidebar; kept current as samples arrive.
    private Guid? _scorePopupConnectionId;

    // A click on a score in the drop-down opens its breakdown instead of switching connection:
    // the button takes the click, so the item isn't selected.
    private void HealthScore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConnectionSelectorItem item })
            return;

        e.Handled = true;
        CmbConnection.IsDropDownOpen = false;

        // After the drop-down has closed and let go of the mouse, or the popup closes at once.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            _scorePopupConnectionId = item.Settings.Id;
            ShowScoreBreakdown(item);
            ScorePopup.IsOpen = true;
        });
    }

    private void ShowScoreBreakdown(ConnectionSelectorItem item)
    {
        var session = Monitoring.Get(item.Settings.Id);
        ScoreBreakdown.Show(item.DisplayName, item.Score,
                            session is null ? Monitoring.NotMonitoredReason(item.Settings.Id) : session.HealthText,
                            paused: session is { IsRunning: false });
    }

    private void ScorePopup_Closed(object? sender, EventArgs e) => _scorePopupConnectionId = null;

    private async void CmbConnection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelector || CmbConnection.SelectedItem is not ConnectionSelectorItem { Settings: var target })
            return;
        if (target.Id == _activeConnectionId)
            return;

        if (!await SwitchToAsync(target.Id, ActivitySources.ConnectionSelector))
        {
            // Keep the selector on the connection that is still active.
            RefreshConnectionSelector(SettingsService.Load());
            return;
        }

        // Settings follows the change itself (#131), and a connection's page may hold unsaved changes.
        if (MainFrame.Content is SettingsPage settings)
        {
            settings.ShowMessage($"Switched to \"{target.DisplayName}\".");
            return;
        }

        // Reopen the current page as a fresh instance so nothing from the previous database
        // stays on screen. A server's page is a new route, so Back returns to the previous
        // server's; a page about every server is the same route again (#139).
        var current = _history.Current;
        await NavigateTo(current?.Tag ?? "LiveMetrics", alertsConnectionId: current?.AlertsConnectionId,
                         serverId: current?.ServerId);
    }

    /// <summary>
    /// Makes a saved connection the active one, as the selector does: every page then queries it.
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

    private async void ManageConnections_Click(object sender, RoutedEventArgs e) =>
        await NavigateTo("Settings", settingsLocation: new SettingsLocation(SettingsCategory.Connections));

    // Pages Azure SQL Database has nothing for (see the wiki's Server Impact and Permissions →
    // Compatibility), with their own tooltip, captured on first use, to restore on another server.
    private (Button Button, string Tag, object? ToolTip, string Reason)[]? _pagesAzureSqlLacks;
    private (Button Button, string Tag, object? ToolTip, string Reason)[] PagesAzureSqlLacks => _pagesAzureSqlLacks ??=
    [
        // TempDB file-level reporting relies on sys.master_files, which Azure SQL Database doesn't expose.
        AzureSqlLacks("TempDb",    "Not available on Azure SQL Database (sys.master_files is not accessible)"),
        // There is no SQL Server Agent, so msdb has no jobs (#39).
        AzureSqlLacks("AgentJobs", "Not available on Azure SQL Database, which has no SQL Server Agent. Elastic Jobs run scheduled work there."),
        // Azure takes the backups itself and records none in msdb (#40).
        AzureSqlLacks("Backups",   "Not available on Azure SQL Database, which backs up every database itself; the Azure portal shows its restore points."),
        // No sysadmin role, sa login or TRUSTWORTHY setting, and the logins are in the logical server's master (#43).
        AzureSqlLacks("SecurityAudit", "Not available on Azure SQL Database, which has no sysadmin role, sa login or TRUSTWORTHY setting; its logins are in the logical server's master database."),
    ];

    private (Button Button, string Tag, object? ToolTip, string Reason) AzureSqlLacks(string tag, string reason) =>
        (NavButton(tag), tag, NavButton(tag).ToolTip, reason);

    // Disable those pages up front instead of letting the user hit a query error after navigating to one.
    private async System.Threading.Tasks.Task DisablePagesAzureSqlLacksAsync()
    {
        if (!_isConnectionConfigured)
            return;

        try
        {
            if (!await Repo.IsAzureSqlDatabaseAsync())
                return;

            foreach (var page in PagesAzureSqlLacks)
            {
                page.Button.IsEnabled = false;
                page.Button.ToolTip   = page.Reason;
                ToolTipService.SetShowOnDisabled(page.Button, true);
            }
        }
        catch
        {
            // Edition check failed (e.g. server unreachable) — leave the pages enabled.
        }
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
    internal static string FormatErrorStatus(Exception ex) =>
        ConnectionSettingsService.RedactSecrets(ex is WaitStatsException wse
            ? $"Error [{wse.ErrorTag}]: {ex.InnerException?.Message ?? ex.Message}"
            : $"Error: {ex.Message}");

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

    // ── Home and server details (#130) ────────────────────────────────────────

    private HomePage CreateHomePage()
    {
        var page = new HomePage(Monitoring, SettingsService);
        page.OpenRequested          += async id => await NavigateTo("ServerDetail", serverId: id);
        page.AddConnectionRequested += async () => await NavigateTo("Settings", settingsLocation: SettingsLocation.NewConnectionPage);
        return page;
    }

    private ServerDetailPage CreateServerDetailPage(Guid connectionId)
    {
        var page = new ServerDetailPage(connectionId, Monitoring, SettingsService);
        page.BackRequested       += async () => await NavigateTo("Home");
        page.EditRequested       += async id => await NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id));
        page.ThresholdsRequested += async id => await NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id, ConnectionTab.Thresholds));
        page.AlertsRequested     += async id => await NavigateTo("Alerts", alertsConnectionId: id);
        page.MakeActiveRequested += async id =>
        {
            if (await SwitchToAsync(id, ActivitySources.Home))
                Bar.ShowMessage($"Switched to \"{SettingsService.Load().Connections.FirstOrDefault(c => c.Id == id)?.DisplayName}\".");
        };
        // A page about the server, e.g. Processes for its blocking chain. It queries the active
        // connection, so this one is made active first.
        page.OpenPageRequested += async (id, tag) =>
        {
            if (!await SwitchToAsync(id, ActivitySources.Home))
                return;
            await NavigateTo(tag);
        };
        return page;
    }

    // Settings (#131): links out of it come back here, and switching theme opens it again.
    private SettingsPage CreateSettingsPage(SettingsLocation? location, string? message)
    {
        var page = new SettingsPage(SettingsService, Monitoring, Chat, ApplySavedConnectionsAsync,
                                    location ?? new SettingsLocation(_settingsCategory), message);
        page.CategoryShown   += category => _settingsCategory = category;
        page.ServerRequested += async id => await NavigateTo("ServerDetail", serverId: id);
        page.ThemeChanged    += async () => await NavigateTo("Settings", settingsLocation: new SettingsLocation(SettingsCategory.Appearance));
        return page;
    }

    /// <summary>Opens another page, as its sidebar button would; for links between pages.</summary>
    internal System.Threading.Tasks.Task OpenPageAsync(string tag) => NavigateTo(tag);

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
            return;
        }
        Bar.ShowContext(Freshness(context), context.CanPause, DatabaseOf(context));
    }

    private PageFreshness Freshness(IPageContext page) => page.Kind switch
    {
        PageKind.Live     => new(PageKind.Live, page.LiveInterval, page.IsPaused, AsOf: _updatedAt),
        PageKind.Snapshot => new(PageKind.Snapshot, Taken: _updatedAt),
        PageKind.History  => new(PageKind.History, Window: page.HistoryWindow, CompareWith: page.CompareWith, DataTo: page.DataTo),
        _                 => new(PageKind.None),
    };

    // The active connection's database, as the selector shows it, on a page that reads it; null
    // for a connection from appsettings.json only, whose database isn't known here.
    private string? DatabaseOf(IPageContext page) =>
        page.UsesDatabase ? _selectorItems.FirstOrDefault(i => i.Settings.Id == _activeConnectionId)?.DatabaseLabel : null;

    // ── Navigation (#139) ─────────────────────────────────────────────────────

    /// <summary>Opens a page as a new visit: the sidebar, a link, the tray, the selector.</summary>
    /// <param name="settingsMessage">Shown at the top of Settings.</param>
    /// <param name="settingsLocation">Where Settings opens (#131); the category last used when null.</param>
    /// <param name="serverId">The connection a server detail page (#130) shows.</param>
    private System.Threading.Tasks.Task NavigateTo(string tag, string? settingsMessage = null, SettingsLocation? settingsLocation = null,
                                                   Guid? alertsConnectionId = null, Guid? serverId = null) =>
        OpenAsync(RouteFor(tag, settingsLocation, alertsConnectionId, serverId), settingsMessage, _history.Visit);

    // A page queries the active connection unless it is about every connection, or one by id.
    private Route RouteFor(string tag, SettingsLocation? settingsLocation = null, Guid? alertsConnectionId = null, Guid? serverId = null) =>
        new(tag,
            ConnectionId:       Route.IsGlobal(tag) ? null : _activeConnectionId,
            ServerId:           tag == "ServerDetail" ? serverId : null,
            AlertsConnectionId: tag == "Alerts" ? alertsConnectionId : null,
            Settings:           tag == "Settings" ? settingsLocation : null);

    // Alt+← / Alt+→ and the keyboard's own Back and Forward keys, before the Frame's built-in
    // journal commands see them (its journal is kept empty). F5 refreshes the page (#140), rather
    // than the Frame reloading it.
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await RefreshPageAsync();
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

    // The mouse's back and forward buttons.
    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
            return;
        e.Handled = true;
        await GoAsync(back: e.ChangedButton == MouseButton.XButton1);
    }

    /// <summary>
    /// Back or Forward to the nearest route still available, switching to its connection first
    /// when it's another server's page. Routes skipped on the way are dropped from the history.
    /// </summary>
    private async System.Threading.Tasks.Task GoAsync(bool back)
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
    private async System.Threading.Tasks.Task OpenAsync(Route route, string? settingsMessage, Action<Route> record)
    {
        // A connection's page, or the default thresholds, may hold unsaved changes: ask first, and
        // stay when the user says so (#131). The page navigated to, as the Frame shows it a moment later.
        if (_page is SettingsPage leaving && !await leaving.TryLeaveAsync())
            return;

        SaveViewState();

        // Back or Forward to another server's page: that server becomes the active one again, as
        // the selector would make it. Without its saved password, Settings opens to ask for it.
        if (route.ConnectionId is { } connectionId && connectionId != _activeConnectionId &&
            !await SwitchToAsync(connectionId, ActivitySources.BackOrForward))
            return;

        var version = ++_loadVersion;
        var tag = route.Tag;
        var settingsLocation = route.Settings;

        // A server detail page needs its server; without one, Home lists them all.
        if (tag == "ServerDetail" && route.ServerId is null)
            tag = "Home";

        // Without a connection every data page would just fail — point the user at Settings.
        // Alerts only reads the local history, and a notification can lead there with none active.
        // Home and a server's details only read the sessions and the history too (#130).
        if (!_isConnectionConfigured && !Route.IsGlobal(tag))
        {
            tag = "Settings";
            settingsMessage ??= SettingsService.Load().Connections.Count == 0
                ? "No SQL Server connection is configured yet. Click + New connection to add one, then Save & make active."
                : "No connection is active. Choose a saved connection in the sidebar selector, or add a new one.";
            settingsLocation ??= new SettingsLocation(SettingsCategory.Connections);
        }

        // A page Azure SQL Database doesn't have, e.g. on a server just switched to: Live Metrics.
        if (IsDisabled(tag))
            tag = "LiveMetrics";

        var shown = tag == route.Tag ? route : RouteFor(tag, settingsLocation);
        record(shown);
        UpdateHistoryButtons();

        // Highlight the page's button, and show it if its group is collapsed. A server's details
        // are under Home.
        var button = tag == "ServerDetail" ? "Home" : tag;
        System.Diagnostics.Debug.Assert(_sidebar.HasPage(button), $"No sidebar button for \"{tag}\": add it to NavMenu.");
        _sidebar.Highlight(_sidebar.HasPage(button) ? button : "LiveMetrics");

        // Settings page does not implement IRefreshable — handle separately
        if (tag == "Settings")
        {
            var settings = CreateSettingsPage(settingsLocation, settingsMessage);
            MainFrame.Navigate(settings);
            _page      = settings;
            _updatedAt = null;
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
            "ServerDetail"    => CreateServerDetailPage(shown.ServerId!.Value),
            "LiveMetrics"     => new LiveMetricsDashboardPage(Repo, Monitoring, _activeConnectionId, Monitoring.HistoryFor(_activeConnectionId), activeName),
            "Alerts"          => new AlertsPage(Monitoring, SettingsService, shown.AlertsConnectionId),
            "TopWaits"        => new TopWaitsPage(Repo),
            "ActiveWaits"     => new ActiveWaitsPage(Repo),
            "Processes"       => new ProcessesPage(Repo, SettingsService),
            "Deadlocks"       => new DeadlocksPage(Repo),
            "AgentJobs"       => new AgentJobsPage(Repo),
            "Backups"         => new BackupsPage(Repo, SettingsService),
            "FileIo"          => new FileIoPage(Repo, SettingsService),
            "ConfigChecks"    => new ConfigChecksPage(Repo, activeName),
            "SecurityAudit"   => new SecurityAuditPage(Repo, activeName),
            "WaitTrend"       => new WaitStatsTrendPage(Repo, Monitoring.HistoryFor(_activeConnectionId)),
            "TempDb"          => new TempDbPage(Repo),
            "Memory"          => new MemoryGrantsPage(Repo),
            "QueryStore"      => new QueryStorePage(Repo, SettingsService),
            "QueryRegressions" => new QueryRegressionsPage(Repo, Monitoring.HistoryFor(_activeConnectionId), SettingsService),
            "IndexHealth"     => new IndexHealthPage(Repo),
            "ResourceQueries" => new ResourceQueriesPage(Repo),
            "ImplicitConv"    => new ImplicitConversionsPage(Repo),
            "PlanCacheHealth" => new PlanCacheHealthPage(Repo),
            "StaleStats"      => new StaleStatisticsPage(Repo),
            "DbStorage"       => new DatabaseStoragePage(Repo),
            "AppConnections" => new ApplicationConnectionsPage(Repo),
            "Perfmon"        => new PerfmonPage(Repo, Monitoring.HistoryFor(_activeConnectionId)),
            "SpTrace"         => new SpTracePage(Repo),
            "Export"          => new ExportPage(Repo),
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

        MainFrame.Navigate(page);
        _page       = page as Page;
        _pageLoaded = false;
        _updatedAt  = null;
        Bar.ShowPage(tools, Freshness(context), context.CanPause, DatabaseOf(context));

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
            MessageBox.Show(
                FormatErrorDetail(ex) + "\n\nIf the server details are wrong, update the connection in Settings — no restart needed.",
                "Data Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private bool IsDisabled(string tag) => PagesAzureSqlLacks.Any(p => p.Tag == tag && !p.Button.IsEnabled);

    // Which routes Back and Forward can still open: no connection they name has been removed,
    // and the active server has the page. Another server's Azure pages are only known once
    // switched to, and then Live Metrics opens instead. The saved connections are the selector's,
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
        ConnectionSelectorItem? Item(Guid id) => _selectorItems.FirstOrDefault(i => i.Settings.Id == id);
        Bar.ShowBreadcrumb(Breadcrumb.For(current, id => Item(id)?.DisplayName), Item);
    }

    // "Processes · PROD-SQL01", "PROD-SQL01" for its details, "Alerts" for a page about every server.
    private string Describe(Route route)
    {
        string? Name(Guid? id) => id is null ? null : _selectorItems.FirstOrDefault(i => i.Settings.Id == id)?.DisplayName;

        if (route.Tag == "ServerDetail")
            return Name(route.ServerId) ?? "Server";

        var label = NavMenu.AllPages.FirstOrDefault(p => p.Tag == route.Tag)?.Label ?? route.Tag;
        return Name(route.ConnectionId ?? route.AlertsConnectionId) is { } server ? $"{label} · {server}" : label;
    }
}
