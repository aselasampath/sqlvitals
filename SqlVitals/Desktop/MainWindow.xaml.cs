using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Repositories;
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

    // Page the user is on, reopened against the new database after a switch.
    private string _currentTag = "LiveMetrics";

    // Bumped on every navigation and connection switch. A page load that finishes after
    // either has happened belongs to a previous database, so its result is discarded.
    private int _loadVersion;

    // Set while the selector is repopulated in code, so it doesn't trigger a switch.
    private bool _isUpdatingSelector;

    // Background live-metrics collection for the active and every monitored connection.
    internal readonly MonitoringManager Monitoring = new();
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
        Repo = BuildRepository(active, store.CommandTimeoutSeconds);

        _tray = new TrayIcon(SettingsService);
        _tray.OpenRequested   += ShowFromTray;
        _tray.AlertsRequested += async connectionId =>
        {
            ShowFromTray();
            await NavigateTo("Alerts", alertsConnectionId: connectionId);
        };
        _tray.ExitRequested   += ExitApp;

        Monitoring.SessionStateChanged += OnMonitoringStateChanged;
        Monitoring.AlertsChanged       += OnAlertsChanged;
        SyncConnections(store);

        Loaded += async (_, _) =>
        {
            if (!_isConnectionConfigured)
            {
                // First run, or the active connection was removed: send the user to Settings
                // instead of letting every page fail against an empty connection string.
                await NavigateTo("Settings", store.Connections.Count == 0
                    ? "Welcome to SqlVitals! Enter your SQL Server connection details below, then click Save & Connect."
                    : "No connection is active. Select a saved connection in the sidebar, or add a new one below.");
                return;
            }

            if (active is { NeedsPassword: true })
            {
                // "Remember password" was off last session, so every query would fail the login.
                await NavigateTo("Settings", PasswordPrompt(active), active.Id);
                return;
            }

            await DisableTempDbIfAzureSqlAsync();
            await NavigateTo("LiveMetrics");
        };
    }

    private static string PasswordPrompt(ConnectionSettings conn) =>
        $"Enter the password for {conn.UserName} on {conn.Server} (\"{conn.DisplayName}\"), then click Save & Connect.";

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
        SyncConnections(store);

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

        BtnTempDb.IsEnabled = true;
        BtnTempDb.ToolTip = null;
        await DisableTempDbIfAzureSqlAsync();
    }

    // ── Connection selector ───────────────────────────────────────────────────

    // Brings background monitoring and the selector in line with the saved connections.
    private void SyncConnections(ConnectionStore store)
    {
        Monitoring.Sync(store);
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
        BtnAlerts.Content = active.Count == 0 ? "🔔  Alerts" : $"🔔  Alerts ({active.Count})";
        BtnAlerts.ToolTip = active.Count switch
        {
            0     => "Health thresholds breached, now and in the past, for every monitored connection",
            1     => "1 active alert",
            var n => $"{n} active alerts",
        };
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

        var store = SettingsService.Load();

        if (target.NeedsPassword)
        {
            // Switching would only fail the login — ask for the password first, and keep the
            // selector on the connection that is still active.
            RefreshConnectionSelector(store);
            await NavigateTo("Settings", PasswordPrompt(target), target.Id);
            return;
        }

        try
        {
            SettingsService.SetActive(target.Id);
        }
        catch (Exception ex)
        {
            RefreshConnectionSelector(store);
            TxtStatus.Text = $"Could not switch: {ConnectionSettingsService.RedactSecrets(ex.Message)}";
            return;
        }

        store = SettingsService.Load();
        SyncConnections(store);
        await SwapRepositoryAsync(store);

        // Reopen the current page as a fresh instance so nothing from the previous database
        // stays on screen. TempDB may have just been disabled for an Azure SQL database.
        var tag = _currentTag == "TempDb" && !BtnTempDb.IsEnabled ? "LiveMetrics" : _currentTag;
        await NavigateTo(tag, tag == "Settings" ? $"Switched to \"{target.DisplayName}\"." : null, target.Id);
    }

    private async void ManageConnections_Click(object sender, RoutedEventArgs e) =>
        await NavigateTo("Settings");

    // TempDB file-level reporting relies on sys.master_files, which Azure SQL Database
    // doesn't expose (see README → Azure SQL vs On-Premises Compatibility). Disable the
    // tab up front instead of letting the user hit a query error after navigating to it.
    private async System.Threading.Tasks.Task DisableTempDbIfAzureSqlAsync()
    {
        if (!_isConnectionConfigured)
            return;

        try
        {
            if (await Repo.IsAzureSqlDatabaseAsync())
            {
                BtnTempDb.IsEnabled = false;
                BtnTempDb.ToolTip = "Not available on Azure SQL Database (sys.master_files is not accessible)";
            }
        }
        catch
        {
            // Edition check failed (e.g. server unreachable) — leave the tab enabled.
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
            "Monitoring carries on and alerts still notify you. Click the SqlVitals icon here to open it, " +
            "or right-click it and choose Exit to quit.");
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

        _tray.Dispose();

        // Background collectors hold no server-side state; just stop them polling.
        Monitoring.Dispose();

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

    private async void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
            await NavigateTo(btn.Tag?.ToString() ?? "Overview");
    }

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

    private async System.Threading.Tasks.Task NavigateTo(string tag, string? settingsMessage = null, Guid? settingsConnectionId = null,
                                                         Guid? alertsConnectionId = null)
    {
        var version = ++_loadVersion;

        // Without a connection every data page would just fail — point the user at Settings.
        // Alerts only reads the local history, and a notification can lead there with none active.
        if (!_isConnectionConfigured && tag is not ("Settings" or "Alerts"))
        {
            tag = "Settings";
            settingsMessage ??= SettingsService.Load().Connections.Count == 0
                ? "No SQL Server connection is configured yet. Enter your connection details below, then click Save & Connect."
                : "No connection is active. Select a saved connection in the sidebar, or add a new one below.";
        }

        _currentTag = tag;

        // Update nav button styles
        foreach (var btn in new[] { BtnLiveMetrics, BtnAlerts, BtnTopWaits, BtnActiveWaits, BtnProcesses, BtnWaitTrend, BtnTempDb, BtnMemory, BtnQueryStore, BtnRegressions, BtnIndexHealth, BtnResQueries, BtnImpConv, BtnPlanHealth, BtnStaleStats, BtnDbStorage, BtnAppConn, BtnPerfmon, BtnSpTrace, BtnExport, BtnSettings })
            btn.Style = (Style)FindResource("NavButton");

        Button active = tag switch
        {
            "Alerts"          => BtnAlerts,
            "TopWaits"        => BtnTopWaits,
            "ActiveWaits"     => BtnActiveWaits,
            "Processes"       => BtnProcesses,
            "WaitTrend"       => BtnWaitTrend,
            "TempDb"          => BtnTempDb,
            "Memory"          => BtnMemory,
            "QueryStore"      => BtnQueryStore,
            "QueryRegressions" => BtnRegressions,
            "IndexHealth"     => BtnIndexHealth,
            "ResourceQueries" => BtnResQueries,
            "ImplicitConv"    => BtnImpConv,
            "PlanCacheHealth" => BtnPlanHealth,
            "StaleStats"      => BtnStaleStats,
            "DbStorage"       => BtnDbStorage,
            "AppConnections" => BtnAppConn,
            "Perfmon"        => BtnPerfmon,
            "SpTrace"         => BtnSpTrace,
            "Export"          => BtnExport,
            "Settings"        => BtnSettings,
            _                 => BtnLiveMetrics,
        };
        active.Style = (Style)FindResource("NavButtonActive");

        // Settings page does not implement IRefreshable — handle separately
        if (tag == "Settings")
        {
            MainFrame.Navigate(new SettingsPage(SettingsService, Monitoring, ApplySavedConnectionsAsync, settingsMessage, settingsConnectionId));
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = _isConnectionConfigured ? "Settings" : "Not connected";
            return;
        }

        var activeName = _selectorItems.FirstOrDefault(i => i.Settings.Id == _activeConnectionId)?.DisplayName;
        IRefreshable page = tag switch
        {
            "LiveMetrics"     => new LiveMetricsDashboardPage(Repo, Monitoring.Get(_activeConnectionId), Monitoring.HistoryFor(_activeConnectionId), activeName),
            "Alerts"          => new AlertsPage(Monitoring, SettingsService, alertsConnectionId),
            "TopWaits"        => new TopWaitsPage(Repo),
            "ActiveWaits"     => new ActiveWaitsPage(Repo),
            "Processes"       => new ProcessesPage(Repo),
            "WaitTrend"       => new WaitStatsTrendPage(Repo, Monitoring.HistoryFor(_activeConnectionId)),
            "TempDb"          => new TempDbPage(Repo),
            "Memory"          => new MemoryGrantsPage(Repo),
            "QueryStore"      => new QueryStorePage(Repo),
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
            _                 => new LiveMetricsDashboardPage(Repo, Monitoring.Get(_activeConnectionId), Monitoring.HistoryFor(_activeConnectionId), activeName),
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
