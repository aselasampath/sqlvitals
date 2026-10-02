using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Controls;
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

    // Page the user is on, reopened against the new database after a switch; and the server a
    // server detail page shows (#130).
    private string _currentTag = "LiveMetrics";
    private Guid?  _currentServerId;

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
        // stays on screen. The page may have just been disabled for an Azure SQL database.
        var tag = PagesAzureSqlLacks.Any(p => p.Tag == _currentTag && !p.Button.IsEnabled) ? "LiveMetrics" : _currentTag;
        await NavigateTo(tag, serverId: _currentServerId);
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
            TxtStatus.Text = $"Could not switch: {ConnectionSettingsService.RedactSecrets(ex.Message)}";
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
    // log; the status bar shows the one-line error. A page the user has navigated away from
    // no longer owns the status bar, so only its log entry is kept.
    internal static void ReportBackgroundError(Page page, string source, Exception ex)
    {
        AppLog.Error($"{source}: collection failed", ex);
        if (Window.GetWindow(page) is MainWindow main && ReferenceEquals(main.MainFrame.Content, page))
            main.TxtStatus.Text = FormatErrorStatus(ex);
    }

    // Replaces an earlier background error once the page collects successfully again.
    internal static void ReportBackgroundSuccess(Page page)
    {
        if (Window.GetWindow(page) is MainWindow main && ReferenceEquals(main.MainFrame.Content, page))
            main.TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss}";
    }

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
                TxtStatus.Text = $"Switched to \"{SettingsService.Load().Connections.FirstOrDefault(c => c.Id == id)?.DisplayName}\".";
        };
        // A page about the server, e.g. Processes for its blocking chain. It queries the active
        // connection, so this one is made active first.
        page.OpenPageRequested += async (id, tag) =>
        {
            if (!await SwitchToAsync(id, ActivitySources.Home))
                return;
            var disabled = PagesAzureSqlLacks.Any(p => p.Tag == tag && !p.Button.IsEnabled);
            await NavigateTo(disabled ? "LiveMetrics" : tag);
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

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (MainFrame.Content is IRefreshable page)
        {
            var version = _loadVersion;
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = "Refreshing…";
            try
            {
                await page.RefreshAsync();
                if (version == _loadVersion)
                    TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                AppLog.Error($"{_currentTag}: refresh failed", ex);
                if (version == _loadVersion)
                    TxtStatus.Text = FormatErrorStatus(ex);
            }
            finally
            {
                if (version == _loadVersion)
                    BtnRefresh.IsEnabled = true;
            }
        }
    }

    /// <param name="settingsMessage">Shown at the top of Settings.</param>
    /// <param name="settingsLocation">Where Settings opens (#131); the category last used when null.</param>
    /// <param name="serverId">The connection a server detail page (#130) shows.</param>
    private async System.Threading.Tasks.Task NavigateTo(string tag, string? settingsMessage = null, SettingsLocation? settingsLocation = null,
                                                         Guid? alertsConnectionId = null, Guid? serverId = null)
    {
        // A connection's page, or the default thresholds, may hold unsaved changes: ask first, and
        // stay when the user says so (#131).
        if (MainFrame.Content is SettingsPage leaving && !await leaving.TryLeaveAsync())
            return;

        var version = ++_loadVersion;

        // A server detail page needs its server; without one, Home lists them all.
        if (tag == "ServerDetail" && serverId is null)
            tag = "Home";

        // Without a connection every data page would just fail — point the user at Settings.
        // Alerts only reads the local history, and a notification can lead there with none active.
        // Home and a server's details only read the sessions and the history too (#130).
        if (!_isConnectionConfigured && tag is not ("Settings" or "Alerts" or "Home" or "ServerDetail"))
        {
            tag = "Settings";
            settingsMessage ??= SettingsService.Load().Connections.Count == 0
                ? "No SQL Server connection is configured yet. Click + New connection to add one, then Save & make active."
                : "No connection is active. Choose a saved connection in the sidebar selector, or add a new one.";
            settingsLocation ??= new SettingsLocation(SettingsCategory.Connections);
        }

        _currentTag      = tag;
        _currentServerId = tag == "ServerDetail" ? serverId : null;

        // Highlight the page's button, and show it if its group is collapsed. A server's details
        // are under Home.
        var button = tag == "ServerDetail" ? "Home" : tag;
        System.Diagnostics.Debug.Assert(_sidebar.HasPage(button), $"No sidebar button for \"{tag}\": add it to NavMenu.");
        _sidebar.Highlight(_sidebar.HasPage(button) ? button : "LiveMetrics");

        // Settings page does not implement IRefreshable — handle separately
        if (tag == "Settings")
        {
            MainFrame.Navigate(CreateSettingsPage(settingsLocation, settingsMessage));
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = _isConnectionConfigured ? "Settings" : "Not connected";
            return;
        }

        var activeName = _selectorItems.FirstOrDefault(i => i.Settings.Id == _activeConnectionId)?.DisplayName;
        IRefreshable page = tag switch
        {
            "Home"            => CreateHomePage(),
            "ServerDetail"    => CreateServerDetailPage(serverId!.Value),
            "LiveMetrics"     => new LiveMetricsDashboardPage(Repo, Monitoring, _activeConnectionId, Monitoring.HistoryFor(_activeConnectionId), activeName),
            "Alerts"          => new AlertsPage(Monitoring, SettingsService, alertsConnectionId),
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

        MainFrame.Navigate(page);

        BtnRefresh.IsEnabled = false;
        TxtStatus.Text = "Loading…";
        try
        {
            await page.RefreshAsync();
            if (version == _loadVersion)
                TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            AppLog.Error($"{tag}: load failed", ex);

            // The user has already moved on (another page or another database) — an error
            // from this load would describe data that is no longer on screen.
            if (version != _loadVersion)
                return;

            TxtStatus.Text = FormatErrorStatus(ex);
            MessageBox.Show(
                FormatErrorDetail(ex) + "\n\nIf the server details are wrong, update the connection in Settings — no restart needed.",
                "Data Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (version == _loadVersion)
                BtnRefresh.IsEnabled = true;
        }
    }
}
