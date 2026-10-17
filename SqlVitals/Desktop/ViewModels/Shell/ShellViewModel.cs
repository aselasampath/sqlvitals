using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.ViewModels.Shell;

/// <summary>
/// The main window's logic (#191): the saved servers and the active one, the page on screen from
/// the page factory with Back and Forward (#139), the breadcrumb, what the page reports
/// (<see cref="IShellStatus"/>), the server workspace (#141), focus mode (#148), and the app's
/// services started and stopped with the window: monitoring, the chat channels and the collector
/// service. The top bar, the sidebar and the quick switcher are its parts.
/// </summary>
/// <remarks>
/// The window is its <see cref="IPageHost"/>: the page itself, the tray icon, the window's
/// placement and the keys stay there. A test drives it with a fake host.
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "Lives as long as the app; Shutdown cancels and disposes the connection's work")]
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IPageFactory _pages;
    private readonly IDialogService _dialogs;
    private readonly ConnectionSettingsService _settings;
    private readonly MonitoringManager _monitoring;
    private readonly ChatNotificationService _chat;
    private readonly IRepositoryFactory _repositories;
    private readonly CollectorLink _collectors;
    private readonly IWatchStarter _starter;
    private readonly IUiDispatcher _ui;

    private IPageHost _host = NoHost.Instance;

    public ShellViewModel(IPageFactory pages, IDialogService dialogs, ConnectionSettingsService settings, MonitoringManager monitoring,
                          ChatNotificationService chat, IRepositoryFactory repositories, CollectorLink collectors,
                          IWatchStarter starter, IUiDispatcher ui)
    {
        _pages        = pages;
        _dialogs      = dialogs;
        _settings     = settings;
        _monitoring   = monitoring;
        _chat         = chat;
        _repositories = repositories;
        _collectors   = collectors;
        _starter      = starter;
        _ui           = ui;
        Repo          = repositories.Create(repositories.TargetFor(null, 0));

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Sidebar.Version = $"v{version?.Major}.{version?.Minor}.{version?.Build}";
        Title = $"SqlVitals {Sidebar.Version}";

        TopBar.BackRequested     += async () => await Reported(GoAsync(back: true), "go back or forward");
        TopBar.ForwardRequested  += async () => await Reported(GoAsync(back: false), "go back or forward");
        TopBar.RefreshRequested  += async () => await Reported(RefreshPageAsync(), "refresh the page");
        TopBar.PauseRequested    += PausePage;
        TopBar.CrumbRequested    += async route => await Reported(OpenAsync(route, settingsMessage: null, _history.Visit), "open the page");
        TopBar.ServerRequested   += async id => await Reported(SwitchServerAsync(id), "switch server");
        TopBar.DatabaseRequested += async () =>
        {
            if (_activeConnectionId is { } id)
                await Reported(NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id)), "open the page");
        };
        TopBar.SidebarRequested  += Sidebar.ToggleFromBar;
        TopBar.JumpRequested     += OpenSwitcher;
        TopBar.FocusRequested    += ToggleFocus;

        // A page opened as a new visit from the sidebar: on the server whose pages are shown, switching to it if need be.
        Sidebar.PageRequested          += async tag => await Reported(NavigateTo(tag, serverId: WorkspaceServer, switchSource: ActivitySources.Sidebar), "open the page");
        Sidebar.MonitorToggleRequested += ToggleWorkspaceMonitoring;
        Sidebar.ModeChosen             += SaveSidebarMode;
        // ☰ says what it does next.
        Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SidebarViewModel.Shown) or nameof(SidebarViewModel.IsSlidOver))
                ShowSidebarState();
        };

        // What the switcher opens goes through the navigator, so Back returns; the keyboard goes
        // into the new page if it was in the one replaced (the window decides, from where it is).
        Switcher.OpenRequested += async (route, intoPage) =>
        {
            await Reported(OpenAsync(route, settingsMessage: null, _history.Visit, ActivitySources.QuickSwitcher), "open the page");
            if (intoPage)
                FocusIntoPageRequested?.Invoke();
        };
    }

    /// <summary>The window's title: "SqlVitals v0.80.0".</summary>
    public string Title { get; }

    public TopBarViewModel TopBar { get; } = new();

    public SidebarViewModel Sidebar { get; } = new();

    public QuickSwitcherViewModel Switcher { get; } = new();

    /// <summary>The window, which shows the pages; until then, nothing is shown.</summary>
    public void Attach(IPageHost host) => _host = host;

    // ── What the window hears ────────────────────────────────────────────────

    /// <summary>An alert started or turned critical on a connection that notifies: a Windows notification (#35).</summary>
    public event Action<AlertNotification>? AlertNotified;

    /// <summary>Something to tell the user in a Windows notification, even while the window is hidden: title, text, warning.</summary>
    public event Action<string, string, bool>? Informed;

    /// <summary>The quick switcher opened a page while the keyboard was in the page it replaced: the keyboard goes into the new one.</summary>
    public event Action? FocusIntoPageRequested;

    /// <summary>The quick switcher is about to open: menus and pop-ups the window has open close first.</summary>
    public event Action? SwitcherOpening;

    /// <summary>The top bar's details button, or Ctrl+J: the details drawer opens or closes (#147).</summary>
    public event Action? DetailsRequested
    {
        add    => TopBar.DetailsRequested += value;
        remove => TopBar.DetailsRequested -= value;
    }

    // ── The active connection ────────────────────────────────────────────────

    /// <summary>
    /// The active connection's repositories; replaced whenever it changes, so pages created after
    /// that query the new server without restarting the app.
    /// </summary>
    public IWaitStatsRepository Repo { get; private set; }

    private bool _isConnectionConfigured;

    // The shell's own reads of the active connection (its edition); a switch cancels them (#167).
    // A page's reads end with the page (PageLifetime).
    private CancellationTokenSource _connectionWork = new();

    // Identifies the connection Repo was built from, so re-reading the store only swaps the
    // repository when the active connection (or its details) actually changed.
    private Guid? _activeConnectionId;
    private string _activeFingerprint = string.Empty;

    /// <summary>The active connection; null when none is.</summary>
    public Guid? ActiveConnectionId => _activeConnectionId;

    // The saved servers, by name: the server header, the breadcrumb's menu, the switcher.
    private List<ConnectionSelectorItem> _servers = [];

    /// <summary>The saved servers, by name, with their health.</summary>
    public IReadOnlyList<ConnectionSelectorItem> Servers => _servers;

    // The settings at start-up, for the first page.
    private ConnectionStore _startStore = new();

    /// <summary>
    /// Starts what lives with the window: the active connection's repository, the sidebar as it was
    /// left, monitoring, the chat channels and the collector service, all following the settings.
    /// </summary>
    /// <param name="windowWidth">The window's width: a narrow one shows a pinned sidebar collapsed.</param>
    /// <returns>The settings it started from.</returns>
    public ConnectionStore Start(double windowWidth)
    {
        var store = _startStore = _settings.Load();

        // Pinned, collapsed or hidden (#142): as last chosen, collapsed in a narrow window.
        Sidebar.Restore(store.SidebarMode, windowWidth);
        Repo = BuildRepository(store.Active, store.CommandTimeoutSeconds);

        // Every settings change goes to the activity log (#115); never a secret (see SettingsDiff).
        _settings.Changed += OnSettingsChanged;

        _chat.Apply(store);
        _chat.StartedFailing += OnChatChannelFailing;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (store.ChatChannelsDropped)
            Informed?.Invoke("Chat channels were removed",
                "SqlVitals couldn't read its Slack and Teams channels on this PC or Windows account, so alerts " +
                "can't be posted to them. Add them again in Settings → Alerts & chat.", false);

        _monitoring.SessionStateChanged += OnMonitoringStateChanged;
        _monitoring.MonitoringChanged   += OnMonitoringChanged;
        // What the collector service watches, the app stops collecting and reads from its history (#172).
        _collectors.Changed += OnCollectorsChanged;
        _collectors.Start();
        _monitoring.AlertsChanged       += OnAlertsChanged;
        // Separate from the Windows notification: muting a connection doesn't stop its channels (#121).
        _monitoring.AlertsRaised        += _chat.OnAlertsRaised;
        _monitoring.AllStopped          += _chat.OnAllStopped;
        // "Stopped watching" and "watching again" (#170); before the first Sync, which starts some.
        _monitoring.WatchingChanged     += _chat.OnWatchingChanged;
        SyncConnections(store, ActivitySources.AutomaticAtStartup);
        return store;
    }

    private void OnCollectorsChanged()
    {
        var view = _collectors.View;
        _monitoring.SetWatchedElsewhere(view.Watches, view.Texts, view.Sources);
    }

    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows)
    {
        foreach (var row in rows)
            _monitoring.Log(row);

        // Chat channels, and the connection names their messages use, apply from the next
        // alert, and the Backups page's RPO from the backups check's next read (#174). Only reads.
        var saved = _settings.Load();
        _chat.Apply(saved);
        _monitoring.ApplyBackupRpo(saved);
        // The collector service watching some of these servers counts against the same RPO.
        if (rows.Any(r => r.Setting?.StartsWith("Backup RPO", StringComparison.Ordinal) == true))
            _ = Reported(_collectors.ApplyBackupRpoAsync(saved.BackupRpo, saved.Connections.Select(c => c.Id).ToList()),
                         "give the collector service the backup RPO");
        // How query text is kept (#175): from the next texts read, here and in the service.
        if (rows.Any(r => r.Setting == SettingsActivity.QueryTextSetting))
        {
            _monitoring.ApplyHistorySettings(saved);
            _ = Reported(_collectors.ApplyQueryTextStorageAsync(saved.QueryTextStorage, saved.Connections.Select(c => c.Id).ToList()),
                         "give the collector service the query text setting");
        }
    }

    /// <summary>
    /// The first page, once the window is on screen: where SqlVitals was left, or Settings when no
    /// server can be opened yet (the first-run flow with nothing saved, #177), or to ask for a
    /// password that wasn't saved.
    /// </summary>
    [ReportsItsErrors]
    public async Task OpenFirstPageAsync()
    {
        var store = _startStore;
        if (!_isConnectionConfigured)
        {
            // First run, or the active connection was removed: send the user to Settings
            // instead of letting every page fail against an empty connection string.
            await Reported(store.Connections.Count == 0
                ? NavigateTo("Settings", settingsLocation: SettingsLocation.FirstRunFlow)
                : NavigateTo("Settings", "No server is open. Open one from All servers, or add a new connection.",
                             new SettingsLocation(SettingsCategory.Connections)), "open the page");
            return;
        }

        if (store.Active is { NeedsPassword: true } active)
        {
            // "Remember password" was off last session, so every query would fail the login.
            await Reported(NavigateTo("Settings", PasswordPrompt(active), SettingsLocation.Connection(active.Id)), "open the page");
            return;
        }

        await Reported(DisablePagesAzureSqlLacksAsync(), "check the server's edition");
        await Reported(OpenAsync(StartRoute(store), settingsMessage: null, _history.Visit), "open the page");
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

    private static string PasswordPrompt(ConnectionSettings conn) =>
        $"Enter the password for {conn.UserName} on {conn.Server} (\"{conn.DisplayName}\"), then click Save & make active.";

    // Builds the repository the pages use, from appsettings.json overlaid with the active connection.
    private IWaitStatsRepository BuildRepository(ConnectionSettings? active, int commandTimeoutSeconds)
    {
        _activeConnectionId = active?.Id;
        _activeFingerprint  = RepositoryFactory.Fingerprint(active, commandTimeoutSeconds);

        var target = _repositories.TargetFor(active, commandTimeoutSeconds);
        _isConnectionConfigured = !string.IsNullOrWhiteSpace(target.ConnectionString);
        return _repositories.Create(target);
    }

    /// <summary>
    /// Re-reads the saved connections after Settings changed them. Swaps in a new repository
    /// when the active connection changed, refreshes the server header, and optionally opens the
    /// dashboard — so connection changes apply without restarting the app.
    /// </summary>
    public async Task ApplySavedConnectionsAsync(bool navigateToDashboard)
    {
        var store = _settings.Load();
        SyncConnections(store, ActivitySources.Settings);

        if (RepositoryFactory.Fingerprint(store.Active, store.CommandTimeoutSeconds) != _activeFingerprint)
            await SwapRepositoryAsync(store);

        // A connection renamed or removed: the breadcrumb and Back and Forward follow (#139), and
        // the database the page reads (#140).
        UpdateHistoryButtons();
        ShowPageContext();

        // "Save & make active": the server's Overview (#143).
        if (navigateToDashboard)
            await NavigateTo("Overview");
    }

    private async Task SwapRepositoryAsync(ConnectionStore store)
    {
        // Anything still loading was queried against the previous database.
        _loadVersion++;
        _model?.OnNavigatedFrom();
        var stopped = _connectionWork;
        _connectionWork = new CancellationTokenSource();
        stopped.Cancel();
        stopped.Dispose();

        var previous = Repo;
        Repo = BuildRepository(store.Active, store.CommandTimeoutSeconds);

        // Best effort: a trace session started against the old connection shouldn't be
        // left running on that server.
        _ = Task.Run(async () =>
        {
            try { await previous.StopTraceAsync(); } catch { /* old server may be unreachable */ }
        });

        await DisablePagesAzureSqlLacksAsync();

        // Back to a page this server doesn't have is skipped (#139).
        UpdateHistoryButtons();
    }

    /// <summary>
    /// Makes a saved connection the active one: every server page then queries it. Opening one of
    /// a server's pages does this (#141).
    /// False when it couldn't: its password isn't saved (Settings is opened to ask for it), or the
    /// change couldn't be saved (the top bar says why).
    /// </summary>
    /// <param name="source">Where the user switched, for the activity log.</param>
    public async Task<bool> SwitchToAsync(Guid connectionId, string source)
    {
        if (connectionId == _activeConnectionId)
            return true;

        var store  = _settings.Load();
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
            _settings.SetActive(target.Id, source);
        }
        catch (Exception ex)
        {
            TopBar.ShowMessage($"Could not switch: {ConnectionSettingsService.RedactSecrets(ex.Message)}", isError: true);
            return false;
        }

        store = _settings.Load();
        SyncConnections(store, source);
        await SwapRepositoryAsync(store);
        return true;
    }

    // Another server from the breadcrumb's menu: the same page there, or its Overview if it
    // hasn't got it (see OpenAsync). Settings is opened to ask for a password that isn't saved.
    private async Task SwitchServerAsync(Guid server)
    {
        if (_history.Current is { } current)
            await OpenAsync(Workspace.OnServer(current, server), settingsMessage: null, _history.Visit, ActivitySources.ServerMenu);
    }

    // ── Pages Azure SQL Database lacks (#141) ────────────────────────────────

    /// <summary>
    /// Pages Azure SQL Database has nothing for (see the wiki's Server Impact and Permissions →
    /// Compatibility), with the reason shown on their button.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PagesAzureSqlLacks = new Dictionary<string, string>
    {
        // TempDB file-level reporting relies on sys.master_files, which Azure SQL Database doesn't expose.
        ["TempDb"]    = "Not available on Azure SQL Database (sys.master_files is not accessible)",
        // There is no SQL Server Agent, so msdb has no jobs (#39).
        ["AgentJobs"] = "Not available on Azure SQL Database, which has no SQL Server Agent. Elastic Jobs run scheduled work there.",
        // Azure takes the backups itself and records none in msdb (#40).
        ["Backups"]   = "Not available on Azure SQL Database, which backs up every database itself; the Azure portal shows its restore points.",
        // No sysadmin role, sa login or TRUSTWORTHY setting, and the logins are in the logical server's master (#43).
        ["SecurityAudit"] = "Not available on Azure SQL Database, which has no sysadmin role, sa login or TRUSTWORTHY setting; its logins are in the logical server's master database.",
    };

    // The active connection is Azure SQL Database: checked after every switch.
    private bool _activeIsAzureSql;

    private bool IsDisabled(string tag) => _activeIsAzureSql && PagesAzureSqlLacks.ContainsKey(tag);

    // The sidebar shows them disabled while the open server is the active one. Another server's
    // edition is only known once it is switched to, by opening one of its pages; one it lacks then
    // opens its Overview instead (see OpenAsync).
    private void ApplyPagesAzureSqlLacks()
    {
        var known = WorkspaceServer is null || WorkspaceServer == _activeConnectionId;
        Sidebar.Unavailable = known && _activeIsAzureSql ? PagesAzureSqlLacks : new Dictionary<string, string>();
    }

    // Disable those pages up front instead of letting the user hit a query error after navigating to one.
    private async Task DisablePagesAzureSqlLacksAsync()
    {
        _activeIsAzureSql = false;
        if (_isConnectionConfigured)
        {
            var cancel = _connectionWork.Token;
            try { _activeIsAzureSql = await Repo.IsAzureSqlDatabaseAsync(cancel); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; /* switched meanwhile: the new server's check decides */ }
            catch { /* Edition check failed (e.g. server unreachable): leave the pages enabled. */ }
        }
        ApplyPagesAzureSqlLacks();
    }

    // ── Saved servers and their health ───────────────────────────────────────

    // Brings monitoring, the server header and the breadcrumb's menu in line with the saved
    // connections. The source says why, for the activity log rows of any connection that starts or stops.
    private void SyncConnections(ConnectionStore store, string source)
    {
        _monitoring.Sync(store, source);
        _servers = store.Connections
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ConnectionSelectorItem(c))
            .ToList();
        foreach (var item in _servers)
            item.UpdateHealth(_monitoring);
        ShowWorkspace();
    }

    private ConnectionSelectorItem? Item(Guid? id) => id is null ? null : _servers.FirstOrDefault(i => i.Settings.Id == id);

    /// <summary>
    /// The server whose workspace is open: the one the page on screen is inside. Null on a page
    /// about every server (Home, Alerts, Settings).
    /// </summary>
    public Guid? WorkspaceServer => _history.Current is { } route ? Workspace.ServerOf(route) : null;

    // The sidebar's server part follows the page: its server's header and pages, or how to open one.
    private void ShowWorkspace()
    {
        Sidebar.ShowServer(Item(WorkspaceServer));
        ApplyPagesAzureSqlLacks();
    }

    // Start or Stop in the server header: the same as ▶ / ■ on its Home tile.
    private void ToggleWorkspaceMonitoring()
    {
        if (WorkspaceServer is not { } id)
            return;
        if (_monitoring.IsMonitoring(id))
            _monitoring.Stop(id, ActivitySources.Sidebar);
        else
            _ = _starter.StartAsync(id, ActivitySources.Sidebar);   // reports its own errors
    }

    private void OnMonitoringStateChanged(Guid connectionId)
    {
        if (Item(connectionId) is not { } item)
            return;

        item.UpdateHealth(_monitoring);
        // The server header (#141) and the breadcrumb's server part (#140) show its health and score.
        if (connectionId == WorkspaceServer)
            Sidebar.ShowServer(item);
        if (_history.Current is { } current && (connectionId == WorkspaceServer || connectionId == current.AlertsConnectionId))
            UpdateBreadcrumb();
        if (_scoreConnectionId == connectionId)
            ShowScoreCard(item);
    }

    // ── Alerts, and the tray icon (#35) ──────────────────────────────────────

    /// <summary>The active alerts of every monitored connection, for the tray icon.</summary>
    public IReadOnlyCollection<Alert> ActiveAlerts => _monitoring.ActiveAlerts;

    /// <summary>How many connections are monitored, for the tray icon.</summary>
    public int MonitoredCount => _monitoring.MonitoredCount;

    private void OnAlertsChanged(Guid connectionId, IReadOnlyList<AlertChange> changes)
    {
        UpdateAlerts();

        // Only an alert starting or turning critical notifies; the settings are only read then.
        if (!changes.Any(c => c.Kind is AlertChangeKind.Started or AlertChangeKind.Escalated))
            return;

        var store = _settings.Load();
        if (!store.NotifiesFor(connectionId))
            return;

        var name = store.Connections.FirstOrDefault(c => c.Id == connectionId)?.DisplayName ?? "a deleted connection";
        if (AlertNotification.From(connectionId, name, changes) is { } notification)
            AlertNotified?.Invoke(notification);
    }

    // Alerts with a count while any monitored connection has an active alert; the tray icon says the same.
    private void UpdateAlerts()
    {
        OnPropertyChanged(nameof(ActiveAlerts));
        Sidebar.AlertCount = _monitoring.ActiveAlerts.Count;
        // On ☰ while the sidebar is hidden.
        ShowSidebarState();
    }

    // Monitoring started or stopped somewhere: the tray says whether anything is monitored.
    private void OnMonitoringChanged()
    {
        OnPropertyChanged(nameof(MonitoredCount));
        UpdateAlerts();
    }

    // Once each time a chat channel starts failing, even while the window is hidden, and whatever
    // connections are muted: it's about the channel (#122).
    private void OnChatChannelFailing(Guid channelId, string text) => Informed?.Invoke(ChannelStatus.FailingTitle, text, true);

    /// <summary>The tray menu's Start all and Stop all.</summary>
    public void StartAll() => _monitoring.StartAll(ActivitySources.TrayMenu);

    /// <inheritdoc cref="StartAll"/>
    public void StopAll() => _monitoring.StopAll(ActivitySources.TrayMenu);

    /// <summary>The tray menu's Alerts opens every server's; a notification opens its server's workspace (#141).</summary>
    [ReportsItsErrors]
    public Task OpenAlertsAsync(Guid? connectionId) =>
        connectionId is { } id && Item(id) is not null
            ? Reported(NavigateTo("Overview", serverId: id), "open the page")
            : Reported(NavigateTo("Alerts", alertsConnectionId: connectionId), "open the page");

    /// <summary>A notification about one alert opens its evidence (#145).</summary>
    [ReportsItsErrors]
    public Task InvestigateAsync(Alert alert, string source) => Reported(OpenEvidenceAsync(alert, source), "open the alert's evidence");

    /// <summary>Closing the window leaves SqlVitals running in the notification area (Settings).</summary>
    public bool KeepRunningInTray => _settings.Load().KeepRunningInTray;

    /// <summary>
    /// The first time the window goes to the notification area, what to say about it; null after
    /// that. Monitoring, history and alerts carry on; only the window goes.
    /// </summary>
    public string? TrayHint()
    {
        if (_settings.Load().TrayHintShown)
            return null;
        try { _settings.SetTrayHintShown(); }
        catch (Exception ex) { AppLog.Error("Tray: could not save that the hint was shown", ex); }
        return (_monitoring.MonitoredCount > 0
                   ? "Monitoring carries on and alerts still notify you. "
                   : "Nothing is being monitored; start it from the icon's menu. ") +
               "Click the SqlVitals icon here to open it, or right-click it and choose Exit to quit.";
    }

    // ── Query text (#175) ────────────────────────────────────────────────────

    /// <summary>An installation from before #175 kept query text in full: its user chooses once, when the window is first on screen.</summary>
    public bool AsksQueryTextStorage => !_startStore.QueryTextChoiceAsked;

    /// <summary>Asks how query text is kept, once; closed without a choice keeps it as it was: in full.</summary>
    public void AskQueryTextStorage()
    {
        try
        {
            var choice = _dialogs.AskQueryTextStorage() ?? QueryTextStorage.Full;
            _settings.SetQueryTextStorage(choice, ActivitySources.Settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save how query text is kept", ex);
        }
    }

    // ── Sleep and exit (#170) ────────────────────────────────────────────────

    /// <summary>How long the PC's sleep waits for the chat channels' "stopped watching" (#170).</summary>
    private static readonly TimeSpan SleepTime = TimeSpan.FromSeconds(2);

    // Raised on a SystemEvents thread; OnResumed is thread-safe, the rest runs on the UI thread.
    // Going to sleep waits here, holding Windows back up to SleepTime, so the channels hear that
    // nothing is watched before the network goes; what isn't sent goes when the PC wakes.
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            {
                var waited = System.Diagnostics.Stopwatch.StartNew();
                _ui.Invoke(_chat.OnGoingToSleep, SleepTime);
                var left = SleepTime - waited.Elapsed;
                if (left > TimeSpan.Zero)
                    _chat.WhenIdleAsync().Wait(left);
            }
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                _chat.OnResumed();
                _ui.Post(_chat.OnWokeUp);
            }
        }
        catch (Exception ex)
        {
            // Nothing here may stop the PC sleeping, or reach SystemEvents' thread.
            AppLog.Error("Chat: couldn't tell the channels the PC is sleeping or waking", ex);
        }
    }

    /// <summary>
    /// SqlVitals is quitting: monitoring stops polling, each chat channel gets one "stopped
    /// watching" message saying why (#123, #170), and a live SP trace session left running on the
    /// server is dropped.
    /// </summary>
    public void Shutdown(WatchStop why)
    {
        // A status report still queued for the UI thread mustn't reach the window's disposed icon.
        _chat.StartedFailing -= OnChatChannelFailing;
        // A static event: it would keep this alive.
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        // Background collectors hold no server-side state; just stop them polling. Each chat
        // channel's message sends while the history is flushed; then whatever is still waiting
        // gets the rest of a few seconds.
        var exitStarted = System.Diagnostics.Stopwatch.StartNew();
        _chat.BeginExit(why);
        _collectors.Dispose();
        _monitoring.Dispose();
        _chat.Shutdown(ChatNotificationService.ExitTime - exitStarted.Elapsed);
        _chat.Dispose();
        _connectionWork.Cancel();
        _connectionWork.Dispose();

        // Last line of defence for the live SP trace: SP Trace's view model drops its event session
        // when the user navigates away, but exiting while that page is open doesn't navigate away.
        // Without this the session keeps collecting on the monitored server after the app is gone.
        try
        {
            // Stop the page's poll timer first. Each poll holds the trace repository's gate
            // for a full server round trip, so leaving it running eats the budget below and
            // the session survives the app — which is what it did before this call existed.
            if (_model is SpTraceViewModel trace)
                trace.PrepareForShutdown();

            // Task.Run keeps the await continuations off the UI thread — blocking on them
            // here with a live SynchronizationContext would deadlock. The budget covers a
            // cold connection to a remote server (Azure SQL routinely needs several
            // seconds) plus the DROP itself.
            var repo = Repo;
            Task.Run(() => repo.StopTraceAsync()).Wait(TimeSpan.FromSeconds(20));
        }
        catch
        {
            // Shutting down regardless. A session left behind is dropped by name on the
            // next Start Trace.
        }
    }

    // ── Health score (#36) ───────────────────────────────────────────────────

    // The connection whose breakdown is open beside the sidebar; kept current as samples arrive.
    private Guid? _scoreConnectionId;

    /// <summary>The open server's breakdown, while it is open.</summary>
    public ScoreCard? Score { get; private set; }

    /// <summary>The score in the server header was clicked: its breakdown. False with no server open.</summary>
    public bool ShowWorkspaceScore()
    {
        if (Item(WorkspaceServer) is not { } item)
            return false;
        _scoreConnectionId = item.Settings.Id;
        ShowScoreCard(item);
        return true;
    }

    /// <summary>The breakdown closed.</summary>
    public void CloseScore() => _scoreConnectionId = null;

    private void ShowScoreCard(ConnectionSelectorItem item)
    {
        var session = _monitoring.Get(item.Settings.Id);
        Score = new ScoreCard(item.DisplayName, item.Score,
                              session is null ? _monitoring.NotMonitoredReason(item.Settings.Id) : session.HealthText,
                              Paused: session is { IsRunning: false });
        // Raised every time: the card shows the latest sample.
        OnPropertyChanged(nameof(Score));
    }

    // ── Focus mode (#148) ────────────────────────────────────────────────────

    /// <summary>
    /// Full screen without the sidebar: the window goes full screen, a floating drawer closes, the
    /// top bar stays with Exit focus. Nothing is saved: the sidebar's chosen state is kept to come back to.
    /// </summary>
    public bool InFocus { get; private set; }

    /// <summary>F11, the Focus button, or Exit focus.</summary>
    public void ToggleFocus()
    {
        if (InFocus) ExitFocus();
        else         EnterFocus();
    }

    public void EnterFocus()
    {
        if (InFocus)
            return;
        Sidebar.InFocus = true;
        InFocus = true;
        TopBar.InFocus = true;
        OnPropertyChanged(nameof(InFocus));
    }

    /// <summary>Back to the window and the sidebar as they were. False when not in focus mode.</summary>
    public bool ExitFocus()
    {
        if (!InFocus)
            return false;
        InFocus = false;
        Sidebar.InFocus = false;
        TopBar.InFocus = false;
        OnPropertyChanged(nameof(InFocus));
        return true;
    }

    // A layout preference: saved, but not in the activity log (see SettingsActivity).
    private void SaveSidebarMode(SidebarMode mode)
    {
        // Pinned from the menu slid in over a focus-mode page: the user wants it, so focus ends.
        ExitFocus();
        try { _settings.SetSidebarMode(mode); }
        catch (Exception ex) { AppLog.Error("Sidebar: could not save whether it is pinned, collapsed or hidden", ex); }
    }

    private void ShowSidebarState() => TopBar.ShowSidebar(Sidebar.Shown, Sidebar.IsSlidOver, _monitoring.ActiveAlerts.Count);

    // ── What the page on screen reports (IShellStatus, #183) ─────────────────
    // Timed refreshes report through the top bar rather than a message box, which would pop up
    // again on every tick while the server stays unreachable (#140). A page the user has left no
    // longer owns the bar, so only its log entry is kept.

    // The page on screen, or its view model: a report from any other is only logged.
    private bool IsOnScreen(object page) =>
        ReferenceEquals(page, _model) || (_shown?.Context is { } context && ReferenceEquals(page, context));

    /// <summary>The page read: opened or refreshed, the top bar's message goes; on its own, only an error does.</summary>
    public void ShowRead(object page, PageRead read)
    {
        if (!IsOnScreen(page))
            return;
        _updatedAt = DateTime.Now;
        if (read != PageRead.Timed || TopBar.MessageIsError)
            TopBar.ShowMessage(null);
        ShowPageContext();
    }

    /// <summary>The page couldn't read: the error on the top bar, and in a message box when it was just opened.</summary>
    public void ShowError(object page, Exception error, PageRead read)
    {
        if (!IsOnScreen(page))
            return;
        TopBar.ShowMessage(ErrorStatus.Format(error), isError: true);
        if (read == PageRead.Load)
            _dialogs.Warn(
                FormatErrorDetail(error) + "\n\nIf the server details are wrong, update the connection in Settings — no restart needed.",
                "Data Error");
    }

    /// <summary>The page's kind, window or pause changed.</summary>
    public void ShowContextOf(object page)
    {
        if (IsOnScreen(page))
            ShowPageContext();
    }

    /// <summary>
    /// Awaits what one of the shell's events started (a page to open, Back, a server to switch to),
    /// and reports what it throws (#169): on the top bar, and in the log. Its handlers await this.
    /// </summary>
    /// <param name="what">What failed, as the top bar says it: "open the page".</param>
    [ReportsItsErrors]
    private async Task Reported(Task work, string what)
    {
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
            // Another move or switch replaced it: nothing to say.
        }
        catch (Exception ex)
        {
            ShowFailure(what, ex);
        }
    }

    /// <inheritdoc cref="Reported(Task, string)"/>
    /// <returns>Its result, or the default after a failure.</returns>
    [ReportsItsErrors]
    private async Task<T?> Reported<T>(Task<T> work, string what)
    {
        try
        {
            return await work;
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (Exception ex)
        {
            ShowFailure(what, ex);
            return default;
        }
    }

    private void ShowFailure(string what, Exception ex)
    {
        AppLog.Error($"Could not {what}", ex);
        TopBar.ShowMessage($"Could not {what}: {ConnectionSettingsService.RedactSecrets(ex.Message)}", isError: true);
    }

    // Returns the full message for the message box, with the searchable tag on its own line.
    private static string FormatErrorDetail(Exception ex)
    {
        if (ex is WaitStatsException wse)
        {
            var sqlNote = wse.SqlErrorNumber != 0 ? $"\nSQL error number: {wse.SqlErrorNumber}" : string.Empty;
            // A failure SqlVitals tells apart says what it was, and a missing permission the GRANT that fixes it (#167).
            var explained = wse.Failure.Explanation is { } plain ? $"{plain}\n\n" : string.Empty;
            var fix = wse.FixScript is { } script ? $"\n\nA DBA can fix it once with:\n{script}" : string.Empty;
            return ConnectionSettingsService.RedactSecrets(
                $"Error tag:  {wse.ErrorTag}{sqlNote}\n\n{explained}{ex.InnerException?.Message ?? ex.Message}{fix}\n\nSearch the codebase for \"{wse.ErrorTag}\" to locate the originating query.");
        }
        return ConnectionSettingsService.RedactSecrets(ex.Message);
    }

    // ── The links out of a page that lead to the shell (#130, #131, #143) ────

    private void Wire(PageViewModel model)
    {
        switch (model)
        {
            case HomeViewModel home:         Wire(home); break;
            case OverviewViewModel overview: Wire(overview); break;
            case SettingsViewModel settings: Wire(settings); break;
        }
    }

    private void Wire(HomeViewModel page)
    {
        page.OpenRequested          += async id => await Reported(NavigateTo("Overview", serverId: id), "open the page");
        page.InvestigateRequested   += async alert => await Reported(OpenEvidenceAsync(alert, ActivitySources.Home), "open the alert's evidence");
        page.AddConnectionRequested += async () => await Reported(NavigateTo("Settings", settingsLocation: SettingsLocation.NewConnectionPage), "open the page");
        page.EditRequested          += async id => await Reported(NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id)), "open the page");
    }

    private void Wire(OverviewViewModel page)
    {
        page.EditRequested       += async id => await Reported(NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id)), "open the page");
        page.ThresholdsRequested += async id => await Reported(NavigateTo("Settings", settingsLocation: SettingsLocation.Connection(id, ConnectionTab.Thresholds)), "open the page");
        page.AlertsRequested     += async id => await Reported(NavigateTo("Alerts", alertsConnectionId: id), "open the page");
        page.MakeActiveRequested += async id =>
        {
            if (await Reported(SwitchToAsync(id, ActivitySources.Overview), "switch server"))
                TopBar.ShowMessage($"Switched to \"{_settings.Load().Connections.FirstOrDefault(c => c.Id == id)?.DisplayName}\".");
        };
        // A page about the server: Deadlocks, Live charts, the rest of "Since you last looked"… It
        // queries the active connection, so opening it makes this one active (see OpenAsync).
        page.OpenPageRequested += async (id, tag) => await Reported(NavigateTo(tag, serverId: id, switchSource: ActivitySources.Overview), "open the page");
        // An alert's or a check's evidence, filtered and set to its time (#145).
        page.EvidenceRequested += async route => await Reported(OpenAsync(route, settingsMessage: null, _history.Visit, ActivitySources.Overview), "open the page");
    }

    // Settings (#131): links out of it come back here, and switching theme opens it again.
    private void Wire(SettingsViewModel page)
    {
        page.CategoryShown   += category => _settingsCategory = category;
        page.ServerRequested += async id => await Reported(NavigateTo("Overview", serverId: id), "open the page");
        page.ThemeChanged    += async () => await Reported(NavigateTo("Settings", settingsLocation: new SettingsLocation(SettingsCategory.Appearance)), "open the page");
    }

    // ── An alert's evidence (#145) ───────────────────────────────────────────

    /// <summary>
    /// Opens an alert's evidence: its page on its server, filtered to the problem and set to its
    /// time, through the navigator so Back returns. The alert as it stands now: one a notification
    /// was about may have ended since. A deleted connection's alert has no server: Alerts instead.
    /// </summary>
    public async Task OpenEvidenceAsync(Alert alert, string source)
    {
        if (Item(alert.ConnectionId) is null)
        {
            await NavigateTo("Alerts");
            TopBar.ShowMessage("That alert's connection has been removed, so there is no server to look at.");
            return;
        }
        await OpenAsync(EvidenceRoutes.ForAlert(await LatestAsync(alert)), settingsMessage: null, _history.Visit, source);
    }

    private async Task<Alert> LatestAsync(Alert alert)
    {
        if (_monitoring.ActiveAlerts.FirstOrDefault(a => a.Id == alert.Id) is { } active)
            return active;
        if (!alert.IsActive)
            return alert;

        // Not active any more: the history has its end.
        try
        {
            var reader = _monitoring.HistoryReader;
            var stored = await Task.Run(() =>
                reader.ReadAlerts(alert.ConnectionId, alert.StartedUtc.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1)));
            return stored.Select(s => s.Alert).FirstOrDefault(a => a.Id == alert.Id) ?? alert;
        }
        catch (Exception ex)
        {
            AppLog.Error("Evidence: could not read the alert's end from the history", ex);
            return alert;
        }
    }

    /// <summary>
    /// Show all: the page clears its filter where it is, and the history keeps it unfiltered, so
    /// Back and Forward return to it as it is now.
    /// </summary>
    public void ShowAllEvidence()
    {
        if (_history.Current is not { Focus: not null } current)
            return;
        if (_shown?.Focus is { } page)
        {
            try { page.ClearFocus(); }
            catch (Exception ex) { AppLog.Error($"{current.Tag}: could not clear its filter", ex); }
        }
        _history.ReplaceCurrent(EvidenceRoutes.Unfocused(current));
        UpdateHistoryButtons();
        ShowFocusBanner();
    }

    /// <summary>The banner's history link: the page that charts the alert's window.</summary>
    [ReportsItsErrors]
    public async Task OpenEvidenceHistoryAsync()
    {
        if (_history.Current is { } current && EvidenceRoutes.History(current) is { } history)
            await Reported(OpenAsync(history, settingsMessage: null, _history.Visit, ActivitySources.Sidebar), "open the page");
    }

    /// <summary>"Opened from alert" over a page opened as evidence; nothing over any other.</summary>
    public FocusBannerState FocusBanner { get; private set; } = FocusBannerState.None;

    private void ShowFocusBanner()
    {
        var focus = _history.Current?.Focus;
        var page  = _shown?.Focus;
        var text  = focus is null ? null
            : Services.FocusBanner.For(focus, page?.FocusNote, page?.ShowsWindow == true, PageName(focus.HistoryTag), DateTime.Today);
        FocusBanner = new FocusBannerState(text, page?.ShowAllText);
        // Raised every time: the banner hidden on the page left shows again on the next page opened as evidence.
        OnPropertyChanged(nameof(FocusBanner));
    }

    // The server's clock from UTC, from its latest sample; null when it isn't being sampled (see ServerClock).
    private TimeSpan? ServerOffset(Guid? connectionId) =>
        connectionId is { } id && _monitoring.Get(id) is { Samples.Count: > 0, LastSuccess: { } last } session
            ? Engine.Home.ServerClock.Offset(session.Samples[^1].Time, last.ToUniversalTime())
            : null;

    private static string? PageName(string? tag) =>
        tag is null ? null : NavMenu.AllPages.FirstOrDefault(p => p.Tag == tag)?.Label;

    /// <summary>
    /// Opens another page of the same server with a filter, for links between pages that carry it
    /// (#145): a session on Processes' Waiting view, a query on Query Store.
    /// </summary>
    /// <param name="fromPage">The page the link is on, as its banner names it.</param>
    public Task OpenLinkAsync(string tag, FocusFilter filter, string fromPage) =>
        OpenAsync(EvidenceRoutes.Link(tag, _history.Current?.ConnectionId ?? _activeConnectionId, filter, fromPage),
                  settingsMessage: null, _history.Visit, ActivitySources.Page(fromPage));

    /// <summary>Opens another page, as its sidebar button would; for links between pages (<see cref="INavigator"/>).</summary>
    /// <param name="serverId">The server a page is about; null for the active connection.</param>
    public Task OpenPageAsync(string tag, Guid? serverId = null) => NavigateTo(tag, serverId: serverId);

    // ── Quick switcher (#144) ────────────────────────────────────────────────

    /// <summary>Ctrl+K, or the bar's search button: over everything else; a menu the sidebar has open closes first.</summary>
    public void OpenSwitcher()
    {
        SwitcherOpening?.Invoke();
        Sidebar.CloseSlideOver(keyboard: false);
        CloseScore();

        var scope = SwitcherScope();
        Switcher.Open(text => QuickSwitcher.Search(text, scope), id => Item(id),
                      keepsPage: WorkspaceServer is not null && _servers.Count > 1);
    }

    // What the switcher searches: the open server's pages (the active one's on a page about every
    // server, as the sidebar would open them), the saved servers, and the recent routes still available.
    private SwitcherScope SwitcherScope()
    {
        var isAvailable = Availability();
        return new SwitcherScope(
            _history.Current,
            WorkspaceServer ?? _activeConnectionId,
            _servers.Select(i => new SwitcherServer(i.Settings.Id, i.DisplayName, i.Server)).ToList(),
            _recent.Items.Where(isAvailable).ToList())
        {
            // Only the active server's edition is known; another's pages it lacks open its Overview.
            Unavailable = (tag, server) =>
                server == _activeConnectionId && IsDisabled(tag) ? PagesAzureSqlLacks[tag] : null,
        };
    }

    // ── The top bar (#140) ───────────────────────────────────────────────────

    /// <summary>Refresh in the top bar, or F5: the page on screen reads again.</summary>
    [ReportsItsErrors]
    public Task RefreshAsync() => Reported(RefreshPageAsync(), "refresh the page");

    private async Task RefreshPageAsync()
    {
        if (_model is not { } model || !TopBar.CanRefresh)
            return;

        // The view model reports how it went (ShowRead, ShowError), and never throws.
        var version = _loadVersion;
        TopBar.CanRefresh = false;
        TopBar.ShowMessage("Refreshing…");
        await model.RefreshCommand.ExecuteAsync(null);
        if (version == _loadVersion)
            TopBar.CanRefresh = true;
    }

    // Pause or Resume in the top bar, for a live page that can.
    private void PausePage(bool pause)
    {
        if (_shown?.Context is not { CanPause: true } context)
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
        if (_shown?.Context is not { } context)
            TopBar.ShowContext(null, canPause: false, database: null);
        else
            TopBar.ShowContext(Freshness(context), context.CanPause, DatabaseOf(context));
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
        page.UsesDatabase ? Item(_activeConnectionId)?.DatabaseLabel : null;

    // ── Navigation (#139) ────────────────────────────────────────────────────

    // The pages visited, for Back and Forward; its current route is the page on screen, reopened
    // against the new database after a switch.
    private readonly NavigationHistory _history = new();

    // What each recent route's view model kept, put back on the new one (#183).
    private readonly RouteStates<object> _modelStates = new();

    // The routes opened this session, most recent first, for the quick switcher (#144).
    private readonly RecentRoutes _recent = new();

    // Set from Back or Forward until the move is recorded, so a second press doesn't pick the
    // same target from a history that hasn't moved yet.
    private bool _historyMovePending;

    // The page on screen, as it describes itself, and its view model, which loads, refreshes and
    // reports; left, it is cancelled. Loaded or not: one left before then hasn't had its saved view
    // state put back yet, so that state is kept.
    private ShownPage? _shown;
    private PageViewModel? _model;
    private bool _pageLoaded;

    // When the page on screen last loaded, refreshed or reported an update: a snapshot's "Taken",
    // a live page's "As of" (#140). Null until its first load finishes.
    private DateTime? _updatedAt;

    // The Settings category last opened, so Settings opens on it again this session (#131).
    private SettingsCategory _settingsCategory = SettingsCategory.Connections;

    // Bumped on every navigation and connection switch. A page load that finishes after
    // either has happened belongs to a previous database, so its result is discarded.
    private int _loadVersion;

    /// <summary>The route on screen; null before the first page.</summary>
    public Route? Current => _history.Current;

    /// <summary>The page on screen's view model; null before the first page.</summary>
    public PageViewModel? CurrentPage => _model;

    /// <summary>Back or Forward, from the keyboard, the mouse or the top bar.</summary>
    [ReportsItsErrors]
    public Task GoBackOrForwardAsync(bool back) => Reported(GoAsync(back), "go back or forward");

    /// <summary>Opens a page as a new visit: the sidebar, a link, the tray, a Home tile.</summary>
    /// <param name="settingsMessage">Shown at the top of Settings.</param>
    /// <param name="settingsLocation">Where Settings opens (#131); the category last used when null.</param>
    /// <param name="serverId">The server a page is about (#141): an Overview's, or the connection a
    /// server's page queries; null for the active connection.</param>
    /// <param name="switchSource">Why, in the activity log, if opening it switches the active connection.</param>
    private Task NavigateTo(string tag, string? settingsMessage = null, SettingsLocation? settingsLocation = null,
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

    /// <summary>
    /// Back or Forward to the nearest route still available, switching to its connection first
    /// when it's another server's page. Routes skipped on the way are dropped from the history.
    /// </summary>
    public async Task GoAsync(bool back)
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
    private async Task OpenAsync(Route route, string? settingsMessage, Action<Route> record,
                                 string switchSource = ActivitySources.BackOrForward)
    {
        // A page merged into another (#178), as a saved setting or an old link may still name it.
        route = NavMenu.Current(route);

        // A connection's page, or the default thresholds, may hold unsaved changes: ask first, and
        // stay when the user says so (#131).
        if (_model is SettingsViewModel leaving && !await leaving.TryLeaveAsync())
            return;

        // A notification opening a page while the quick switcher is open: the page it was opened over goes.
        Switcher.Close();

        SaveViewState();

        // Another server's page (from its workspace, the server menu, or Back): that server becomes
        // the active one. Without its saved password, Settings opens to ask for it.
        if (route.ConnectionId is { } connectionId && connectionId != _activeConnectionId &&
            !await SwitchToAsync(connectionId, switchSource))
            return;

        var version = ++_loadVersion;
        _model?.OnNavigatedFrom();
        _model = null;
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
            settingsMessage ??= _settings.Load().Connections.Count == 0
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
        System.Diagnostics.Debug.Assert(NavMenu.AllPages.Any(p => p.Tag == tag), $"No sidebar button for \"{tag}\": add it to NavMenu.");
        Sidebar.Current = tag;

        // An unknown tag (one remembered from an older version) opens Live Metrics, as it always has.
        var made = _pages.Create(PageCatalog.Types.ContainsKey(tag) ? tag : "LiveMetrics",
            new PageArgs(tag == "Settings" ? shown with { Settings = settingsLocation ?? new SettingsLocation(_settingsCategory) } : shown, Repo)
            {
                ConnectionId     = _activeConnectionId,
                ConnectionName   = Item(_activeConnectionId)?.DisplayName,
                ServerOffset     = ServerOffset(_activeConnectionId),
                ApplyConnections = ApplySavedConnectionsAsync,
                SettingsMessage  = settingsMessage,
            });
        Wire(made.ViewModel);

        // On screen, with its own controls (a time range picker) in the top bar and its kind; the
        // previous page's go (#140). Opened as evidence (#145): filtered to the problem, or set to
        // the alert's window, from its first load on.
        _shown      = _host.Show(made, shown.Focus);
        _model      = made.ViewModel;
        _updatedAt  = null;

        // Settings reads nothing on Refresh, and keeps its own place (#131).
        if (made.ViewModel is SettingsViewModel)
        {
            ShowFocusBanner();
            TopBar.ShowPage(_shown.Context is { } settings ? Freshness(settings) : null, canPause: false, database: null);
            TopBar.CanRefresh = false;
            if (!_isConnectionConfigured)
                TopBar.ShowMessage("Not connected");
            return;
        }

        var model = made.ViewModel;
        _pageLoaded = false;
        var context = _shown.Context;
        TopBar.ShowPage(context is null ? null : Freshness(context), context?.CanPause == true, context is null ? null : DatabaseOf(context));
        ShowFocusBanner();

        // How the page was left last time: its tab now, each grid's sort, filter and selection
        // as its rows arrive, so the page loads straight into it (#139).
        _host.RestoreViewState(shown, scrolling: false);
        if (_modelStates.Get(shown) is { } modelState)
        {
            try { model.RestoreState(modelState); }
            catch (Exception ex) { AppLog.Error($"{tag}: could not put back the view state", ex); }
        }

        // The view model loads it and reports how it went (ShowRead, ShowError): once the user has
        // moved on (another page or another database) it was cancelled, and says nothing.
        TopBar.CanRefresh = false;
        TopBar.ShowMessage("Loading…");
        await model.OnNavigatedToAsync();
        if (version == _loadVersion)
        {
            TopBar.CanRefresh = true;

            // Loaded or not, the page is as long as it will be: scroll it back.
            _pageLoaded = true;
            _host.RestoreViewState(shown, scrolling: true);
        }
    }

    // Keeps how the page on screen is being looked at, for when its route is opened again.
    // Settings keeps its own place (#131); a page left before it loaded keeps what it had.
    private void SaveViewState()
    {
        if (_history.Current is not { } current || _model is null or SettingsViewModel || !_pageLoaded)
            return;

        try
        {
            _host.KeepViewState(current);
            // What the view model keeps (#183): a converted page's tab or filter.
            if (_model.SaveState() is { } modelState)
                _modelStates.Save(current, modelState);
        }
        catch (Exception ex)
        {
            // Losing a page's sort or scroll position isn't worth stopping the navigation for.
            AppLog.Error($"{current.Tag}: could not keep the view state", ex);
        }
    }

    // The page on screen and its server, opened again at the next start. Not in the activity log.
    // Settings isn't: it is visited, not worked in, and a first run starts there.
    private void RememberPage(Route shown)
    {
        if (shown.Tag == "Settings")
            return;
        try { _settings.SetLastPage(shown.Tag, Workspace.ServerOf(shown)); }
        catch (Exception ex) { AppLog.Error("Sidebar: could not save the page on screen", ex); }
    }

    // Which routes Back and Forward can still open: no connection they name has been removed,
    // and the active server has the page. Another server's Azure pages are only known once
    // switched to, and then its Overview opens instead. The saved connections are the ones held here,
    // kept current as they change, rather than the settings file read again.
    private Func<Route, bool> Availability()
    {
        var ids = _servers.Select(i => i.Settings.Id).ToHashSet();
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
        TopBar.ShowHistory(back is null ? null : Describe(back), forward is null ? null : Describe(forward));
        UpdateBreadcrumb();
    }

    // All servers › PROD-SQL01 › Activity now › Processes, with the server's health.
    private void UpdateBreadcrumb()
    {
        if (_history.Current is not { } current)
            return;
        TopBar.ShowBreadcrumb(Breadcrumb.For(current, id => Item(id)?.DisplayName), id => Item(id), _servers);
    }

    // "Processes · PROD-SQL01", "Overview · PROD-SQL01", "Alerts" for a page about every server.
    private string Describe(Route route) => Breadcrumb.Describe(route, id => Item(id)?.DisplayName);

    // Until the window attaches: nothing is shown.
    private sealed class NoHost : IPageHost
    {
        public static readonly NoHost Instance = new();

        public ShownPage Show(PageInstance page, PageFocus? focus) => new(null, null);
        public void KeepViewState(Route route) { }
        public void RestoreViewState(Route route, bool scrolling) { }
    }
}
