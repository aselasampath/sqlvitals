# Architecture

*For developers and contributors.* How SqlVitals is put together, and how data flows from SQL Server to the screen.

## The big picture

SqlVitals is a **WPF desktop app (.NET 8)** that queries SQL Server DMVs **directly**, in-process. There's no web server, background service or HTTP hop.

```text
┌───────────────────────────── SqlVitals.Desktop (WPF, net8.0-windows) ─────────────────────────────┐
│                                                                                                    │
│  MainWindow ── sidebar, TopBar ── Frame → Pages (IRefreshable, IPageContext)  Controls / Helpers  │
│      │                              │                                                              │
│      │ RepositoryFactory            │ RefreshAsync()                                               │
│      ▼                              ▼                                                              │
│  MonitoringManager ──► MonitoringSession (each monitored one) ──► health dot and score, alerts     │
│                                │                                  └─► TrayIcon: Windows notification │
│  ExportService                 │ samples       ChatNotificationService ─► Slack / Teams (HTTPS)    │
└────────────────────────────────┼───────────────────────────────────────────────────────────────────┘
                                 │ project reference (no HTTP)
┌────────────────────────────────▼──────────────── SqlVitals.Engine (net8.0 library) ───────────────┐
│  Repositories/  WaitStatsRepository, SpTrace-, TempDb-, PlanCacheHealth-, History-, Configuration-,│
│                 QueryRegression-, Deadlock-, AgentJob-, Backup-, FileIo-, SecurityAudit-           │
│                 QueryStore-                                                                        │
│                 ── base: BaseRepository                                                            │
│  Monitoring/    LiveMetricSample, HealthRules, HealthThresholds, HealthScore, BlockingChains       │
│                 MonitoringPlan (which connections are monitored, and why)                          │
│                 ProcessGroups (the Processes grid's login groups, problems first)                  │
│  Deadlocks/     DeadlockReportParser → DeadlockReport, DeadlockPatterns                            │
│  AgentJobs/     AgentJob, AgentJobHistory (runs and steps), MsdbTime                               │
│  Backups/       DatabaseBackup (RPO warnings, recovery point), BackupRpo, BackupHistoryEntry       │
│  FileIo/        FileIoTracker (readings) → FileLatencyList (per file, per volume), thresholds      │
│  ConfigChecks/  ConfigurationChecks: Pass / Warn, recommended values, fix scripts                  │
│  Security/      SecurityAudit: Risk / Review / Pass, the risk explained, fix scripts               │
│  Home/          ServerTile, TileOrder, ScoreTrend, BlockingNow, OutageTimeline, ThingsToCheck      │
│  Settings/      ConnectionCard (a card in Settings → Connections), ThresholdsPreview               │
│  History/       HistoryRecorder → HistoryWriter → HistoryStore (SQLite)   HistoryReader            │
│  Activity/      ActivityEntry (activity log rows), SettingsDiff (a save → rows, no secrets)        │
│  ChatNotifications/ ChannelRouter → ChannelQueue (1/s, RetryPolicy) → ChatFormatter, WebhookUrl    │
│  Alerts/        AlertTracker, Alert, AlertSettings, AlertNotification                               │
│  ExecutionPlans/ ShowplanParser → ExecutionPlan, PlanTreeLayout                                     │
│  Regressions/   RegressionWindows, QueryRegressionDetector                                          │
│  QueryStore/    QueryStorePeriod, time slots, CombinedStdDev, QueryStoreChecks, metrics            │
│  Scripting/     CREATE / DROP / ALTER INDEX / UPDATE STATISTICS generators                          │
│  Filtering/  Export/  Models/  Errors/      Controllers/ + ApiHost (optional REST, unused)           │
└───────────────┬──────────────────────────────────────────────────────┬─────────────────────────────┘
                │ Dapper + Microsoft.Data.SqlClient                    │ Microsoft.Data.Sqlite
                ▼                                                      ▼
      SQL Server / Azure SQL                          %LocalAppData%\SqlVitals\history.db
```

**Key decision:** Desktop references Engine as a **project reference**. There's zero network latency and nothing extra to deploy. Engine also contains `Program.cs` / `ApiHost.cs` and `Controllers/` to run as a standalone REST API in future, but the desktop app doesn't use them. `TODO/wait-stats-ui/` is a parked React web UI and isn't part of the solution.

## Projects

| Project | Framework | Role |
|---|---|---|
| `SqlVitals.Desktop` | `net8.0-windows` (WPF; WinForms only for `NotifyIcon`) | The app users run. Its `<Version>` is the release number, and the title bar shows it. |
| `SqlVitals.Engine` | `net8.0` (`Microsoft.NET.Sdk.Web`) | Models, repositories, history, regressions, scripting, filtering, export; optional REST host |
| `SqlVitals.Engine.Tests` | `net8.0` | xUnit tests for Engine |
| `SqlVitals.Desktop.Tests` | `net8.0-windows` | xUnit tests for the app's services: settings file, monitoring (with a repository that counts calls), the sidebar and the top bar, laid out off screen in both themes (the top bar's states are also rendered to PNGs under `%TEMP%\SqlVitalsTests\top-bar`), the window's layout with the sidebar pinned, collapsed and hidden (`WorkspaceShellTests`, rendered at 1320 × 820 under `%TEMP%\SqlVitalsTests\sidebar-states`), the quick switcher (`QuickSwitcherOverlayTests`, under `%TEMP%\SqlVitalsTests\quick-switcher`), every page on the template with its verdict, folds and drawer (`TemplateRolloutTests`, `TemplateEverywhereTests`, rendered under `%TEMP%\SqlVitalsTests\page-template-rollout`), Home, a server's details and Settings as whole pages on one STA thread with the light theme (`UiThread`), the MVVM rules over the app's sources (`MvvmGuardTests`, with Roslyn; see [MVVM](#mvvm)), the services view models ask for and the verdict actions that open pages (`UiServicesTests`, `VerdictNavigationTests`, with the fakes in `Fakes/`) |
| `SqlVitals.Installer` | `net472` (WPF) | The Setup wizard. .NET Framework so it runs on a clean Windows. |
| `SqlVitals.Installer.Tests` | `net472` | xUnit tests for `Installer/Core` |

## The workspace

From the UI redesign (#138, stories #139–#150) every screen sits in the same window parts, and every page in the sidebar but four is laid out the same way:

```text
┌ MainWindow ──────────────────────────────────────────────────────────────────────────────────┐
│ ┌ WorkspaceShell ──────────────────────────────────────────────────────────────────────────┐ │
│ │ NavSidebar 232 px │ TopBar: ← →, breadcrumb (server ▾), Live / Snapshot / History, ⏸, F5 │ │
│ │  or NavRail 56 px │ FocusBannerView (an alert's evidence)                                │ │
│ │  or hidden        │ DrawerHost ┌ Frame → Page ────────────────────┐ ┌ DetailsDrawer ──┐  │ │
│ │ (SidebarLayout,   │            │ PageShell: title · actions       │ │ IDetailsSource  │  │ │
│ │  Ctrl+B, F11)     │            │   VerdictView  (PageVerdict)     │ │ floating or     │  │ │
│ │                   │            │   FoldSection … (charts, cards)  │ │ pinned, Ctrl+J  │  │ │
│ │                   │            │   detail       (grids, tabs)     │ │                 │  │ │
│ │                   │            └──────────────────────────────────┘ └─────────────────┘  │ │
│ └──────────────────────────────────────────────────────────────────────────────────────────┘ │
│ QuickSwitcherOverlay (Ctrl+K) over it all                                                    │
│ NavigationHistory: Back / Forward, Route, ViewState                                          │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

| Part | What it is | Where |
|---|---|---|
| **Navigator** | One way to open a page: `MainWindow.OpenAsync(route)`. A `Route` is the page tag, its server and what it was opened with (an alert's `PageFocus`). `NavigationHistory` keeps Back and Forward; a page is built fresh on every visit and gets back its tab, sort, filter, selection and scroll from the `ViewState` taken when it was left. | [Showing a screen](#1-showing-a-screen), #139, #145 |
| **Top bar** | `Controls/TopBar`: Back and Forward, the breadcrumb (its server part is a menu of the other servers), and the page's context from `IPageContext`: Live, Snapshot or History, its time, Pause / Resume and Refresh. A page's own pickers move into it (`TakeBarTools`). | #140 |
| **Sidebar states** | `Controls/WorkspaceShell` shows `NavSidebar` pinned (232 px), `NavRail` collapsed (56 px, groups in a `NavFlyout`) or nothing (hidden, slid in from ☰ or the left edge), as the pure `Services/SidebarLayout` says: the saved state, collapsed below 1280 px, Ctrl+B, and hidden in focus mode (F11). | #141, #142, #148 |
| **PageShell** | `Controls/PageShell`: the title and the page's own actions, a `VerdictView` saying what is wrong or that nothing is, `FoldSection`s for charts and cards that fold to one line (remembered per page), then the detail. The verdict is a pure function in `Engine/Verdicts`, put on the shell by the page's view model (`ViewModels/Pages/`, see [MVVM](#mvvm)). | #146, #149, #150, #181 |
| **Details drawer** | `Controls/DrawerHost` around the `Frame`, with `DetailsDrawer` floating over the page or pinned beside it (`Services/DrawerLayout`, Ctrl+J). A page fills it by implementing `IDetailsSource`; most use `Controls/RowDetailsView` with rows from `Engine/Monitoring/RowDetails`. | #147, #149, #150 |

The four pages with layouts of their own are All servers (tiles), a server's Overview (its summary cards), Live charts (full-size charts) and Settings (categories and editors). `TemplateEverywhereTests` fails for any other sidebar page that isn't on `PageShell`.

## Main flows

### 1. Showing a screen

1. A sidebar button's `Tag` (e.g. `"IndexHealth"`) goes to `MainWindow.NavigateTo(tag)`. The sidebar is built from `Services/NavMenu` by `Controls/NavSidebar` (#141): the global pages (All servers, Alerts, Export / AI), the open server's header (name, health, score, Start/Stop) and its pages in one collapsible `Expander` per group (style `NavGroupExpander`, an icon from Segoe Fluent Icons), one group open at a time, and Settings at the foot. The open server is the current route's (`Services/Workspace.ServerOf`); a server page clicked in the sidebar opens on it, switching the active connection (`SwitchToAsync`) if it's another. The breadcrumb's server menu opens the same page on another server (`Workspace.OnServer`), or its Overview when the server lacks it (`Workspace.Available`). `NavigateTo` calls `NavSidebar.Highlight(tag)`, which highlights the button and opens its group. The page on screen and its server are saved (`LastPage`, `LastServerId`, not in the activity log) and reopened at the next start. From #142 the sidebar lives in `Controls/WorkspaceShell`, a `Grid` whose second column is MainWindow's top bar and Frame: pinned it shows `NavSidebar` (232 px), collapsed `Controls/NavRail` (56 px, an icon per global page and group; a group's pages open in a `NavFlyout` on an overlay `Canvas`, not a `Popup`, so focus and hit-testing stay in the window), hidden nothing, with the same `NavSidebar` slid in over the page (column span 2, `TabNavigation=Cycle`) from ☰ or a 6 px hover strip at the left edge. Which state shows is `Services/SidebarLayout` (pure, unit tested): the saved state (`SidebarMode` in `settings.dat`, not in the activity log), shown collapsed instead of pinned below 1280 px, and Ctrl+B / ☰ going on from the state on screen. The 300 ms hover delay and 400 ms leave delay are `DispatcherTimer`s; tests run them with `ElapseTimers()`.
2. `NavigateTo` makes a `Route` (page tag, the active connection for a server's page, and what it was opened with) and opens it: it asks Settings first if it has unsaved changes, keeps the leaving page's `ViewState`, creates the page **fresh**, passing it the repository for the active connection, and navigates the `Frame`. The move is recorded in `Services/NavigationHistory` for **Back** and **Forward** (#139); Back to another server's route switches to it first (`SwitchToAsync`), and routes whose server was removed are skipped. The `Frame`'s own journal is kept empty, so nothing old can come back.
3. It calls the page's `IRefreshable.RefreshAsync()`. **Refresh** in the top bar, and **F5**, call it again.
   **Focus mode and grid density** (#148): F11 or the bar's Focus button captures a `Services/WindowPlacement` (state, style, resize mode, restore bounds) and applies its `FullScreen()` (frameless, maximized, restored first so it covers the taskbar); `WorkspaceShell.InFocus` (`SidebarLayout.InFocus`) shows the sidebar hidden without touching the saved state, and Ctrl+B slides it in instead. Esc, after the quick switcher, a slid-in menu and a floating drawer, or F11 puts the placement back. Grid density sets `GridCellPadding`, `GridHeaderPadding` and `GridFontSize` in the application's own resources (`Services/GridDensity`); the themes define the comfortable values and their DataGrid styles take them as `DynamicResource`, so a change applies to every grid at once and survives a theme switch. Saved as `GridDensity` in `settings.dat` and logged.
   **The details drawer** (#147): `Controls/DrawerHost` (a code-built `Grid`) holds MainWindow's `Frame` and `Controls/DetailsDrawer`, pinned in a second column or floating over the first, as the pure `Services/DrawerLayout` says (Closed / Floating / Pinned; width 320–640; Ctrl+J toggles, Esc closes a floating one). Whether it pins and its width are saved in `settings.dat` (`DrawerPinned`, `DrawerWidth`), not logged. A page fills it by implementing `IDetailsSource` (`Details` → `DrawerContent(Title, Body, Hint)`, `DetailsChanged(open)`); MainWindow shows the page's content on navigation and when it changes, opening the drawer on a click. Processes' `Controls/SessionDetailsView` (rows and KILL script from the pure `Engine/Monitoring/SessionDetails`; the plan from `GetSessionPlanAsync`) and Query Store's `Controls/QueryDetailsView` (`Engine/QueryStore/QueryStoreDetails`) are built from `Controls/DrawerParts`. Resource Queries, Query Regressions and SP Trace (#149), and File I/O, Index Health, Agent Jobs, Config Checks, Security Audit and Alerts (#150) use `Controls/RowDetailsView`: rows from `Engine/Monitoring/RowDetails`, a text with Copy (a query, a fix script, a job step's message) and plans read when asked. The AI assistant (#93) will be another source.
   **The page template** (#146): `Controls/PageShell` (built in code, a `Decorator` whose content is the detail) lays out a page as title and page actions, `Controls/VerdictView`, the `FoldSection`s in `Sections`, then the detail. A verdict is an `Engine/Verdicts/PageVerdict` (level, headline, why, up to four `VerdictAction`s) from a pure function per page (`ProcessesVerdict`, `QueryStoreVerdict`, `BackupsVerdict`), unit tested. A page on the template has a view model in `ViewModels/Pages/` (one file each, named after the page, #181), derived from `ViewModels/PageViewModel` (CommunityToolkit.Mvvm: `[ObservableProperty] Verdict`, `[RelayCommand] Act`) and bound to the shell with `Attach`. The rest of the page's code-behind is unchanged until the page moves to its view model (see [MVVM](#mvvm)). The fold of the verdict and each section is saved per page in `settings.dat` (`ISectionFolds`, implemented by `ConnectionSettingsService`; `FoldedSections`), a layout preference kept out of the activity log. Processes, Query Store and Backups were the pilots; #149 moved the Activity, Waits and Queries pages onto it and #150 the Resources, Maintenance and Configuration pages, Alerts and Export / AI (verdicts in `Engine/Verdicts/ResourcesVerdicts`, `MaintenanceVerdicts`, `ConfigurationVerdicts`, `GlobalVerdicts`). A page's own Start / Stop or Pause went with it where the top bar's Pause / Resume did the same (Wait Trend, Memory Grants, Perfmon, File I/O), and a page's button strip of views became a `TabControl` so the Navigator keeps the tab (Memory Grants, DB Storage). See [The workspace](#the-workspace).
4. The top bar (`Controls/TopBar`, #140) shows the route as a breadcrumb (`Services/Breadcrumb`) and the page's context from its `IPageContext`: its `PageKind` (**Live**, **Snapshot** or **History**; `None` for Settings and Export), a live page's interval and whether it is paused (Pause calls `SetPaused`), a history page's window and where its data ends, and whether it reads the connection's database. A page hands the bar its own controls with `TakeBarTools()` (the three `TimeRangePicker`s, Live Metrics' interval) before it loads, and calls `MainWindow.ReportContextChanged`, `ReportBackgroundSuccess` (the *As of* / *Taken* time) or `ReportBackgroundError` as things change. `PageContextText` words it all, with the clock passed in. `Services/PageCatalog` maps each tag to its page type, and a test fails for a page that doesn't implement `IPageContext`. Short of room the bar leaves out *All servers* and the group, then its freshness text and the 🔍 and Ask AI buttons, then moves the page's controls to a row of its own, so the server's name is never squeezed out.
   **Evidence routes** (#145): a `Route` can carry a `PageFocus`: a filter (`FocusFilter`: a blocking chain, top CPU, a database, pending grants, a session, a query), a time window in UTC, and its origin (an alert, a health check or a page's link). `Services/EvidenceRoutes` (pure, unit tested; it replaced `CheckLinks`) maps every `HealthIndicator` to its page, filter and history page, and builds an alert's route with the window start − 15 min to end + 15 min. `OpenAsync` hands the focus to a page that implements `Pages/IFocusPage` before it loads (Processes, Resource Queries, Memory Grants, DB Storage, Active Waits, Query Store, Live charts); `Controls/FocusBannerView`, between the top bar and the `Frame`, shows *Opened from alert* with the page's `FocusNote` (worded by `FocusBanner.For`), and **Show all** calls `ClearFocus` and `NavigationHistory.ReplaceCurrent` with the unfocused route. `Route.IsSamePlace` ignores the focus, so the same page opened as evidence replaces its entry; the quick switcher's recents and the breadcrumb's other servers drop it. Entry points: the Overview, Home tiles, the Alerts page's Evidence column and a notification about one alert (`AlertNotification.Alert`, `TrayIcon.EvidenceRequested`).
   **Ctrl+K** or 🔍 opens `Controls/QuickSwitcherOverlay` (#144) over the whole window, a child of `WorkspaceShell` spanning both columns (not a `Popup`, like the fly-outs). What it lists is `Services/QuickSwitcher.Search` (pure, unit tested): the open server's pages from `NavMenu` (matched by name, then by the words of their tooltip and group), the same page on the other servers, the saved servers, the global pages, and `SettingsNav`'s categories, connection tabs and settings, ranked exact > prefix > a word's start > anywhere > tooltip words, with the most recent route first on a tie (`RecentRoutes`, this session's). Nothing typed, the 8 most recent routes still available. The result picked goes through `OpenAsync` with `_history.Visit`, so Back returns; its source in the activity log is `user: quick switcher`. The keyboard stays in the search box; Esc gives it back to where it was, and opening a route from inside the page moves it into the new page.
5. The page calls repository methods; results are records from `Engine/Models`, bound to `DataGrid`s and LiveCharts.
6. `Helpers/ViewState` puts back how the route was last looked at (#139): the tabs as the page is built, each grid's filter, sort, selection and scroll position once that grid gets rows, and the page's own scrolling after it has loaded. Elements are matched by `x:Name`, or by order among unnamed ones of a type; anything the user changed meanwhile is left alone. Pages are rebuilt rather than kept because they drop their timers, sessions and traces when they leave the screen; combo boxes and toggles aren't restored, since they can start work. `RouteStates` keeps the last 50 routes' states.
7. Exceptions are caught in `NavigateTo`. Auto-refresh failures go to the top bar and the [log](Settings.md#diagnostics), not to a dialog on every tick.

```csharp
public interface IRefreshable
{
    Task RefreshAsync();
}
```

### 2. Switching connections

`ConnectionSettingsService` loads saved connections (DPAPI) → `RepositoryFactory.Create(connection, commandTimeout)` layers the connection string and timeout over `appsettings.json` → `MainWindow` swaps `Repo` and reopens the current page. In-flight loads for the previous connection are discarded, so stale data never appears under the new name. A **fingerprint** of connection plus timeout decides whether a repository must be rebuilt.

### 3. Monitoring, the health dot and the health score

```text
MonitoringPlan.Decide(Start monitoring automatically, active connection, Monitor in background,
                      Start / Stop pressed this run, password saved, Entra signed in)
   └─► MonitoringManager ─ one MonitoringSession per monitored connection; logs each start and stop
   └─ every interval: repo → LiveMetricSample.From(previous, current)  (counters + ring buffer + requests)
         ├─► HealthRules.Grade(sample, thresholds) → one reading per indicator
         │     ├─► HealthRules.Summarize → (level, reasons) → 🟢 🟠 🔴 dot + tooltip
         │     ├─► HealthScore.From(readings) → 0–100 score + checks → sidebar, Overview, Home, breakdown popup
         │     │     └─► HistoryRecorder.RecordScore → health_scores (failed collections too, as 0)
         │     └─► AlertTracker.Evaluate → alert started / escalated / worsened / ended
         │           └─► HistoryRecorder.RecordAlerts → alerts table;  MonitoringManager.AlertsChanged → Alerts page, sidebar count
         ├─► last 60 samples in memory (for the Overview and Live charts)
         ├─► HistoryRecorder.RecordSample(sample)
         └─► HistoryRecorder.CollectDetailIfDueAsync()
               └─ every N minutes: HistoryRepository detail snapshot → HistoryDetailTracker (deltas)
                     └─► HistoryWriter (bounded queue, one thread) → HistoryStore (SQLite)
```

`HealthRules.Evaluate` returns the worst level any indicator reached and one reason per indicator past its threshold. Page life expectancy and TempDB are skipped when the counter reads 0 (not reported, e.g. Azure SQL Database). Blocking is checked only when a session is blocked. `HealthScore` weights each indicator (CPU, blocking and log space 20, page life expectancy 16, memory grants and TempDB 12); a warning costs half the weight, critical all of it, and a failed collection is `HealthScore.Unavailable` (0). See [Health score](Health-Dot-and-Background-Monitoring.md#health-score). Unreachable servers back off exponentially: `HealthRules.NextDelay` doubles the delay per consecutive failure, capped at 5 minutes. MFA connections, and SQL logins without a saved password, start only after the user has switched to them once.

Which connections are monitored is decided by `MonitoringPlan` (Engine, pure, unit tested): nothing, until the user presses **Start** (on a server's Overview or Live charts, ▶ in the sidebar's server header or on a Home tile, or the tray menu), unless **Start monitoring automatically** is on, in which case the active connection and those with *Monitor in background*. Start and Stop last until the app exits (`MonitoringOverrides`). `MonitoringManager.Sync(store, source)` starts and stops sessions to match, and raises `SessionStateChanged` and `MonitoringChanged` so the sidebar, Home, the Overview, Live charts, the Alerts page's verdict and the tray tooltip all show the same state. See [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring).

### 3a. Home and a server's Overview

`HomePage` (Desktop) builds a `TileInput` per saved connection from the settings and its `MonitoringSession` (score, last success, when it started failing, the error), and `ServerTile.From` (Engine, pure) turns it into what the tile says: status in words, reason, six `CheckBar`s, footer and button. `TileOrder.Sort` puts unreachable and critical first; a new order waits while the mouse is over the tiles. `SessionStateChanged` updates one tile in place. Sparklines read the last hour from `HistoryReader.ReadScores` (the lowest score per bucket) and append the live scores (`ScoreTrend.Merge`); `Controls/ScoreChart` draws them in one pass with `ScoreTrend.Segments` (breaks at gaps) and `UnreachableSpans`. Home sends nothing to a server.

`OverviewPage` (#143; route tag `Overview`, by `ServerId`, so opening it doesn't switch the active connection) replaced `ServerDetailPage` and Live Metrics as a server's first page. Its parts are pure Engine code in `Engine/Home`, unit tested in `Engine.Tests/Home/OverviewTests.cs`: `AlertText.Happening` words an active alert; `ScoreCosts.Lowering` / `Passing` list what is costing the score points from `HealthScore`'s own checks; `Headlines.From` turns the session's samples and the connection's thresholds into CPU, batch requests/s, waiting tasks (a `sys.dm_os_waiting_tasks` count added to the live metrics query) and page life expectancy, each with its level and sparkline; `SinceLastLooked` gives the window (from `Services/OverviewVisits`, which saves when each server's Overview was opened in `settings.dat`, not in the activity log) and filters the deadlocks and failed jobs; `ServerClock.Offset` puts the server's clock on UTC; `MetricScale` rounds a chart's scale. The checks' links and the alerts' Investigate buttons raise `EvidenceRequested(route)` with a route from `EvidenceRoutes` (#145, see above), which `MainWindow` opens on that server, switching the active connection as a sidebar page does. Only *Blocking right now* (`BlockingNow.From` over the session repository's `GetProcessesAsync`, once per sample while blocking is past its warning threshold) and *Since you last looked* (`GetDeadlockHistoryAsync` and `GetAgentJobsAsync`, once per opening) query the server, and only while it is monitored and answering; regressed queries come from `HistoryReader.ReadQueryTotals` and `QueryRegressionDetector`. The charts are `Controls/MetricChart` and `ScoreChart`, drawn in one pass, over the session's samples (live) or `HistoryReader.ReadMetrics` (a past range from the top bar's `TimeRangePicker`, without the baseline), with a `ChartMarker` per alert start. For a server that can't be reached it shows `ThingsToCheck.For(server, error)` and `OutageTimeline.Build` from the session, the activity log and the alerts. Whole-page tests are in `Desktop.Tests/OverviewPageTests.cs`. See [Your Servers](Your-Servers.md#a-servers-overview).

Settings (#131) is a shell, `SettingsPage`: a side menu built from `Services/SettingsNav` (groups, categories, and where every setting the activity log names is changed, checked by a test) and a host that builds each category's `UserControl` in `Pages/Settings/` the first time it's opened. They share a `SettingsContext` (the services, the `Ask` prompt tests answer, links within Settings and out to a server's page). `ConnectionsCategory` shows a card per connection (`ConnectionCard.From` over the session) and a `ConnectionEditor` per connection: four tabs in one `TabControl`, so moving between them keeps what's typed, and one save bar. Unsaved changes are the `ConnectionDraft` of the fields against the saved one; `ConnectionDraft.NeedsServerCheck` decides whether saving signs in first, and the save is one `ConnectionSettingsService.Upsert(settings, notifications)`. Leaving asks through `ISettingsCategory.TryLeaveAsync`, which `MainWindow.NavigateTo` calls before any navigation away from Settings. The thresholds table is `Controls/HealthThresholdsEditor`, laid out like the detail page's Checks table; its preview is `ThresholdsPreview.Regrade`, which grades the latest score's figures with `HealthRules.LevelOf`, the dot's own rule. See [Settings](Settings.md).

### 4. Alerts

`AlertTracker` (one per monitored connection, owned by its `MonitoringSession`) keeps, per indicator, how many samples in a row were past a threshold, critical, or back to normal. An alert starts, escalates to critical or ends when a run reaches the count set in Settings (3 by default). Needing a run to end is the hysteresis that stops a value hovering around a threshold from raising repeated alerts. Stopping the session (a Stop disposes it) ends active alerts as *Monitoring stopped*. Changes go to the history as `AlertRecord`s, stored by alert id, and `HistoryWriter.EndAlertsLeftOpen()` runs at startup for alerts a killed run never ended. See [Alerts](Alerts.md#under-the-hood).

`MainWindow` also turns each alert that starts or escalates into a Windows notification (`AlertNotification` → `TrayIcon`), unless its connection is muted. See [Notifications and Tray](Notifications-and-Tray.md#under-the-hood).

### 5. App lifetime: the notification area and a single instance

`App.OnStartup` makes SqlVitals **single-instance** per Windows session (named mutex; a second copy signals the first to show its window and exits), then opens `MainWindow`. `ShutdownMode` is `OnMainWindowClose`. By default, minimizing or closing `MainWindow` only **hides** it to the notification area (`TrayIcon`). Monitoring carries on, because `MonitoringManager` lives as long as the window object, not its visibility. A real exit (tray **Exit**, or Windows `SessionEnding`) sets `App.IsExiting`, and `OnClosing` then runs the usual shutdown: dispose the tray icon, stop monitoring (ending active alerts, and logging *Monitoring stopped* and *App exited* to the activity log), drop an SP Trace session.

### 5a. The activity log

Every start and stop of monitoring, app start and exit, settings change and history clear becomes an `ActivityEntry`, queued to the history writer as an `ActivityRecord` like samples and alerts, and stored in `activity_log`. Settings changes come from one place: every `ConnectionSettingsService` setter goes through `Update`, which snapshots the store before and after (`SettingsActivity`) and asks `SettingsDiff.Between` for the rows. Secrets are named in `SettingsDiff.SecretNames` and only logged as *changed*. `MonitoringManager.Log` adds the Windows user and PC name. See [Monitoring History Database](Monitoring-History-Database.md#activity_log).

### 6. Execution plans

`QueryExecutionPlanWindow(queryText, planXml)` → `ShowplanParser.Parse` (Engine, pure) → `ExecutionPlan` → `PlanTreeLayout` → `Controls/ExecutionPlanView` draws it. Opened by Resource Queries, SP Trace and Query Regressions. See [Execution Plans](Execution-Plans.md#under-the-hood).

### 7. Reading history

`ConnectionHistory` (Desktop) → `HistoryReader` (Engine) opens its own short-lived read-only SQLite connection on a background thread, buckets the range to ≤ 600 points (`HistoryRange`), marks gaps (`HistoryGaps`), and for baselines shifts the window by 7 days of wall-clock time (`HistoryBaseline`, `BaselineTracker`). See [Monitoring History Database](Monitoring-History-Database.md).

### 8. Query regressions

`QueryRegressionsPage` → `RegressionWindows` (recent + baseline periods) → `QueryRegressionRepository` (Query Store) **or** `HistoryReader` (`query_deltas`) → per-plan stats → `QueryRegressionDetector` with `RegressionCriteria` → rows ordered by extra time. See [Query Regressions](Query-Regressions.md#under-the-hood).

### 8a. Query Store

`QueryStorePage` → `QueryStoreRepository` (delegated from `WaitStatsRepository`; one `sys.all_columns` probe per connection for what the version has, then `sys.database_query_store_options` for the settings) → `QueryStoreChecks.Run` (Engine, pure: the settings checks and their fixes). Each tab reads only when it's opened, after **Refresh** or a new period: the SQL groups `sys.query_store_runtime_stats` by query and plan on the server, ranks with `TOP (@top)`, and returns only those rows. The period is `QueryStorePeriod` (minutes back on the server's clock, or a fixed custom range), Over Time's points come from `QueryStoreTimeSlots`, and High Variation's std dev follows `CombinedStdDev`, the tested reference for the SQL. A missing view or column (207, 208) empties only its tab, with the reason. `QueryStoreQueryWindow` shows one query's intervals and plans. See [Query Performance → Query Store](Query-Performance.md#query-store).

### 9. Deadlock history

`DeadlocksPage` → `DeadlockRepository` (system_health's event files through `sys.fn_xe_file_target_read_file`, filtered to `xml_deadlock_report`; its ring buffer when the files can't be read) → `DeadlockReportParser` (Engine, pure) → `DeadlockReport`s, newest first → `DeadlockPatterns.Group` for the *Seen* count and the Recurring tab. The `.xdl` is the report's `<deadlock>` element. See [Deadlocks](Deadlocks.md#under-the-hood).

### 10. SQL Agent jobs

`AgentJobsPage` → `AgentJobRepository` (edition check, Agent's state, then one row per job from `msdb`: newest step-0 outcome, run counts and average successful duration, the current Agent session's `sysjobactivity`, the earliest enabled schedule) → `AgentJob.From` (Engine, pure: status, running long, next run) → sorted with running long and failed first. Selecting a job reads its `sysjobhistory` rows → `AgentJobHistory.GroupRuns` → runs with their steps. `MainWindow` disables the button on Azure SQL Database, as it does TempDB. See [Agent Jobs](Agent-Jobs.md#under-the-hood).

### 11. Backups and RPO

`BackupsPage` → `BackupRepository` (edition check, then one query: every database in `sys.databases` with its newest full, differential and log backup from `msdb.dbo.backupset` since its `create_date`, ranked and pivoted in one pass over `backupset`, and `sys.database_recovery_status.last_log_backup_lsn`) → `DatabaseBackup.From` (Engine, pure: warnings against the `BackupRpo` saved in `settings.dat`, status, ages, recovery point) → sorted with the red and amber ones first. Selecting a database reads its `backupset` rows. `MainWindow` disables the button on Azure SQL Database. See [Backups](Backups.md#under-the-hood).

### 12. File I/O latency

`FileIoPage` (a `DispatcherTimer` while the page is open) → `FileIoRepository.ReadFileIoAsync` (the edition and `sqlserver_start_time`, then every file's totals from `sys.dm_io_virtual_file_stats` with `SYSDATETIME()`; volumes from `sys.dm_os_volume_stats`, cached per file) → `FileIoTracker.Add` (Engine, pure: keeps the first, previous and last readings, starts again after a restart) → `FileLatencyList.Between` (the change per file between two readings: latency, rates, status against the `FileLatencyThresholds` saved in `settings.dat`; totals per volume). Changing the thresholds or the window re-grades the readings already held. See [File I/O](File-IO.md#under-the-hood).

### 13. Configuration checks

`ConfigChecksPage` → `ConfigurationRepository.GetConfigurationChecksAsync` (the edition and version, `sys.databases`; then on SQL Server and Managed Instance `sys.configurations`, the processors and NUMA nodes from `sys.dm_os_schedulers` and `sys.dm_os_nodes` and the memory from `sys.dm_os_sys_info` when `VIEW SERVER STATE` allows, and `tempdb.sys.database_files`; on Azure SQL Database the `MAXDOP` database scoped configuration) → `ConfigurationChecks.Run` (Engine, pure: Pass, Warn or Not checked per check, the recommended values for this server's processors and memory, explanations and fix scripts) → sorted with the warnings first. Nothing is saved and nothing runs in the background. See [Configuration Checks](Configuration-Checks.md#under-the-hood).

### 14. Security audit

`SecurityAuditPage` → `SecurityAuditRepository.GetSecurityAuditAsync` (the edition, whether only Windows logins are accepted and whether the login has `VIEW ANY DEFINITION`; the sa login by SID; `sys.databases` for TRUSTWORTHY and each owner; the connection's own `sys.login_token`; with `VIEW ANY DEFINITION`, the sysadmin members and CONTROL SERVER grants, and one query per online database for SQL users whose SID has no login) → `SecurityAudit.Run` (Engine, pure: Risk, Review, Pass or Not checked per finding, explanations and fix scripts, a fix that would remove the access SqlVitals is connected with left commented out; Windows' well-known accounts recognised by SID) → sorted with the risks first. Nothing is saved and nothing runs in the background. Azure SQL Database is left out. See [Security Audit](Security-Audit.md#under-the-hood).

## Data access conventions

### BaseRepository wrappers

Every repository derives from `BaseRepository`:

```csharp
protected IDbConnection CreateConnection();                                   // new SqlConnection per call
protected Task<IEnumerable<dynamic>> Q(IDbConnection c, string sql, object? p = null, ...);
protected Task<dynamic?>             QFirst(IDbConnection c, string sql, object? p = null, ...);
protected Task<int>                  Exec(IDbConnection c, string sql, object? p = null, ...); // DDL, no isolation prefix
```

- `Q` / `QFirst` prepend `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;` when `QuerySettings:UseNoLock` is true (default).
- All three apply the command timeout and wrap failures in `WaitStatsException`, which names the repository and method.
- **Always** use `Q` / `QFirst`, never `conn.QueryAsync` directly.

### DBNull-safe mapping

Dapper `dynamic` rows return `DBNull.Value` for SQL `NULL`, and `?? 0m` does **not** catch it:

```csharp
Convert.ToDecimal(r.FileSizeMB)          // ✅ 0m on DBNull
Convert.ToInt64(r.CurrentValue)          // ✅
r.PhysicalPath is DBNull ? "" : (string)r.PhysicalPath   // ✅ nullable strings

(decimal)r.FileSizeMB                    // ❌ InvalidCastException on DBNull
r.FileSizeMB ?? 0m                       // ❌
```

### Where the SQL lives

There are **no `.sql` files**. Each query is a C# raw string literal (`"""…"""`) in the repository that runs it, mostly `Engine/Repositories/WaitStatsRepository.cs`. Copy one into SSMS to try it by hand. Moving SQL to `.sql` resources is tracked in issue #47.

| Feature | Key DMVs |
|---|---|
| Waits | `sys.dm_os_wait_stats`; live: `sys.dm_exec_requests`, `sys.dm_exec_sessions` |
| Processes (blocking chains) | `sys.dm_exec_sessions`, `sys.dm_exec_requests` (`blocking_session_id`), `sys.dm_exec_connections` (`most_recent_sql_handle`), `sys.dm_exec_sql_text`; chains built by `Engine/Monitoring/BlockingChains`; the grid view's login groups by `Engine/Monitoring/ProcessGroups` (same query, no extra load) |
| Deadlocks | `sys.dm_xe_sessions`, `sys.dm_xe_session_targets` (system_health), `sys.fn_xe_file_target_read_file`; parsed by `Engine/Deadlocks/DeadlockReportParser` |
| Backups | `msdb.dbo.backupset`, `backupmediafamily`; `sys.databases`, `sys.database_recovery_status`; worked out by `Engine/Backups` |
| File I/O | `sys.dm_io_virtual_file_stats`, `sys.master_files` (`sys.database_files` on Azure SQL Database), `sys.dm_os_volume_stats`, `sys.dm_os_sys_info`; latency worked out by `Engine/FileIo` from the change between readings |
| Config Checks | `sys.configurations`, `sys.dm_os_schedulers`, `sys.dm_os_nodes`, `sys.dm_os_sys_info`, `tempdb.sys.database_files`, `sys.databases`; `sys.database_scoped_configurations` on Azure SQL Database; checked by `Engine/ConfigChecks` |
| Security Audit | `sys.server_principals` (sa by `sid = 0x01`), `sys.server_role_members`, `sys.server_permissions` (CONTROL SERVER), `sys.sql_logins`, `sys.databases` (`is_trustworthy_on`, owner), `<db>.sys.database_principals` and `sys.schemas` per database; audited by `Engine/Security` |
| Agent Jobs | `msdb.dbo.sysjobs`, `sysjobhistory`, `sysjobactivity`, `syssessions`, `sysjobschedules`, `sysschedules`, `syscategories`; `sys.dm_server_services`, `sys.configurations` (`Agent XPs`); worked out by `Engine/AgentJobs` |
| TempDB | `sys.dm_db_session_space_usage`, `tempdb.sys.database_files` (through `tempdb.sys.sp_executesql`, for `FILEPROPERTY`), `sys.dm_os_performance_counters` |
| Memory | `sys.dm_exec_query_memory_grants`, `sys.dm_os_memory_clerks`, `sys.dm_os_sys_memory`, `sys.dm_os_process_memory` |
| Query Store / regressions | `sys.query_store_*` |
| Indexes | `sys.dm_db_missing_index_*`, `sys.dm_db_index_usage_stats`, `sys.dm_db_index_physical_stats` |
| Plans | `sys.dm_exec_query_stats`, `sys.dm_exec_cached_plans`, `sys.dm_exec_query_plan`, `sys.dm_exec_query_profiles`, `sys.plan_guides` |
| Statistics | `sys.stats`, `sys.dm_db_stats_properties` |
| Storage / config | `sys.master_files`, each database's `sys.database_files` (through `<db>.sys.sp_executesql`, for `FILEPROPERTY`), `sys.configurations`, `sys.database_scoped_configurations` |
| Live metrics / Perfmon / health | `sys.dm_os_performance_counters`, `sys.dm_os_ring_buffers` (`sys.dm_db_resource_stats` on Azure SQL Database), `sys.dm_exec_requests` |
| SP Trace | `sys.dm_xe_*` (Extended Events) or `sys.dm_exec_procedure_stats` |

### Azure SQL Database branching

Server-level views (`sys.master_files`, cross-database `tempdb.sys.database_files`, `sys.configurations`) don't exist on Azure SQL Database (`EngineEdition = 5`). Detect it and switch queries:

```csharp
var edition = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition");
bool isAzureSqlDb = Convert.ToInt32(edition.Edition) == 5;
```

`GetDatabaseStorageAsync` and SP Trace (`IsAzureSqlDatabaseAsync`) follow this pattern. Pages with nothing to show there are listed in `MainWindow.PagesAzureSqlLacks`, each with the reason; on connecting, their sidebar buttons are disabled and given that reason as the tooltip, and the `NavButton` style dims them and marks them **N/A**.

## UI conventions

- **Themes:** `Styles/LightTheme.xaml` (default) and `DarkTheme.xaml`; `IThemeService.Set` swaps the merged dictionary and raises `Changed` (#182).
- Use **brush keys**, never hex: `BgDeep`, `BgCard`, `BgSidebar`, `Accent`, `AccentLight`, `TextPrimary`, `TextMuted`, `TextDisabled`, `Border`, `Success`, `Warning`, `Danger`.
- Style keys: `NavButton` (dimmed, arrow cursor and an **N/A** badge while disabled), `NavButtonActive`, `SectionHeader`, `KpiCard`, `KpiValue`, `KpiLabel`, `RefreshButton`, `SecondaryButton`. `DataGrid` has an implicit style.
- Chart colours come from `Helpers/ChartTheme` (e.g. `ChartTheme.MutedAxisColor`, `ChartTheme.GridColor`) so charts follow the theme.
- Shared building blocks: `Controls/PageShell` with `VerdictView` and `FoldSection` (every page's layout), `RowDetailsView` (a row in the drawer), `Controls/GridFilterBox`, `TimeRangePicker`, `HealthThresholdsEditor`, `HealthScoreBreakdown`, `ProcessMapControl`, `ExecutionPlanView`; `Windows/SqlScriptWindow`, `QueryExecutionPlanWindow`; `Services/TrayIcon`; `Helpers/HealthBrushes` (health colours), `PlanOperatorIcons`, `DataGridExport` (right-click menu), `DataGridRefresh` (keep sort and filter), `BaselineSeries`, `ClipboardHelper`.

## MVVM

Pages are moving from code-behind to view models one at a time (epic #180). Every page that moves follows the rules below, and `Desktop.Tests/MvvmGuardTests.cs` (#181) checks them so that no page slides back.

### Naming and place

| View | View model |
|---|---|
| `Pages/BackupsPage.xaml` | `ViewModels/Pages/BackupsViewModel.cs` |
| `Pages/Settings/AlertsCategory.xaml` | `ViewModels/Settings/AlertsCategoryViewModel.cs` |

- Each view model has its own file, named after the type. Namespaces follow the folders: `SqlVitals.Desktop.ViewModels.Pages` and `SqlVitals.Desktop.ViewModels.Settings`.
- A type that a page's view model owns, such as a row, goes in the same folder, and its name starts with the page's: `BackupsRowViewModel.cs`.
- Shared bases stay in `ViewModels/`. Today that is `PageViewModel`, which holds the verdict and its actions (#146). Every page on the template has a view model derived from it.

### What a view model may use

- **May use:** Engine (models, verdicts, repositories), the Desktop service interfaces in `Services/`, CommunityToolkit.Mvvm, and `LiveChartsCore` (series and axes, not the WPF chart control).
- **May not use:** `System.Windows.Controls`, `MessageBox`, `MainWindow`, `Application.Current` (or `App`), or a `Dispatcher`. A view model asks a service for these instead (see [Services for view models](#services-for-view-models)).
- `PageViewModel.Attach(PageShell)` is the bridge between the shell and the view model. The page calls it once.

### Services for view models

Small interfaces in `Services/` (#182), with WPF implementations in `Services/Wpf/` and fakes in `Desktop.Tests/Fakes/`:

| Service | What it does | WPF implementation |
|---|---|---|
| `IDialogService` | `Confirm` (yes or no, No by default), `AskToSave` (yes, no or cancel), `Inform`, `Warn`, `Error`; opens the SQL script, query plan and Query Store query windows | `WpfDialogService`: message boxes and windows owned by the window in front (the active one, else the main window while shown) |
| `IClipboard` | Copies text | `WpfClipboard`, through `Helpers/ClipboardHelper` (retries a busy clipboard) |
| `IFileDialogs` | `Save` and `Open`, with a `FileDialogOptions`; null when cancelled | `WpfFileDialogs`: Windows' dialogs, owned as above |
| `INavigator` | `GoToAsync(tag)` by `PageCatalog` tag, on a server (#141) or with a focus filter (#145); `InvestigateAsync(alert)`; `BackAsync`, `ForwardAsync` | `ShellNavigator`, through the main window's navigator (#139). An unknown tag throws; with no window it does nothing |
| `IThemeService` | The current theme and `Changed`, for charts and brushes built in code | `WpfThemeService`, over the application's resources |
| `IUiDispatcher` | `Post` and `InvokeAsync` on the UI thread, `IsOnUiThread` | `WpfUiDispatcher`, over the application's dispatcher |

- **The composition root:** `Services/CompositionRoot` builds a `Microsoft.Extensions.DependencyInjection` container (from the ASP.NET Core shared framework the Engine brings) with these services, checked as it is built. `App.OnStartup` builds it before the main window opens. #166 adds settings, monitoring and the repositories to it.
- **Until the page factory (#183):** a page that hands a service to its view model takes it as an optional constructor parameter, which a test fills with a fake, and falls back to `CompositionRoot.Get<T>()`. Controls, windows and helpers that WPF creates ask `CompositionRoot.Get<T>()` directly.
- **A verdict action that opens a page** overrides `OnActionAsync` and returns the navigator's task; one that stays on the page overrides `OnAction`. Active Waits, Alerts, Perfmon, Top Waits, Wait Trend, Query Store and Query Regressions navigate this way.
- Every message box in the app goes through `IDialogService`, and every save dialog through `IFileDialogs`. `ErrorStatus.Format` is the one-line error a verdict and the top bar show.

### Commands

- A button binds `Command="{Binding SaveCommand}"` to a `[RelayCommand]` method. An async method (`Task SaveAsync()`) gets an `IAsyncRelayCommand`, which disables the button while it runs.
- `Click=` is only for view-only actions such as focus, scrolling or opening a popup. A page's XAML may not gain a `Click=`.
- `async void` is only for event handlers. Anywhere else, use a command or a method that returns a `Task`, so that the caller can await it and see what it throws.

### Grids

- A grid binds `ItemsSource` to an `IReadOnlyList<T>` property, and the view model replaces the list on each refresh. A grid that only appends rows uses a range collection.
- The view model owns the selection. It keeps the key of the selected row and selects the row with that key again after a refresh. `DataGridRefresh` doesn't do this.
- The view owns the sort and the filter (`GridFilterBox`). Replacing a bound list makes a new collection view, which drops both. `DataGridRefresh` keeps them for a grid whose rows are set from code-behind, and the pilot (#184) settles how a bound grid keeps them.

### What stays in code-behind

Focus, keyboard handling, scrolling a row into view, drag, custom drawing (the process map, the plan view) and chart glue (LiveCharts events, tooltips, theme colours). Code-behind may forward to the view model, for example a key press that runs a command. It doesn't read data or decide what to show.

### The guard tests

`MvvmGuardTests` reads the app's sources with Roslyn. `DesktopSource` compiles every `.cs` under `Desktop/` against the assemblies the test runs with, and `MvvmRules` holds the checks. A check follows a name to what it refers to: the type itself, the type that owns a member, or the type of a value. So `App.Current`, a field typed as a repository and `session.Repository.GetProcessesAsync()` are all caught, while comments and strings are not.

| Rule | Where | Today's offenders (allowlist) |
|---|---|---|
| A view model doesn't refer to `System.Windows.Controls`, `MessageBox`, `MainWindow`, `Application` or a `Dispatcher` | `ViewModels/**` | `ViewReferencesAllowed`: none |
| Page code doesn't use a repository | `Pages/**/*.cs` | `RepositoryReferencesAllowed`: 26 pages |
| A page's XAML gains no `Click=` (comments aren't counted) | `Pages/**/*.xaml` | `ClicksAllowed`: 98 in 27 files |
| `async void` only in event handlers | all of `Desktop/` | `AsyncVoidAllowed`: 6 in 5 files |
| One file per view model, named after its page | `ViewModels/**` | none |

- A failure names the file and line, for example `Desktop/Pages/ProcessesPage.xaml.cs:43: async void OpenActiveWaits isn't an event handler`.
- An allowlist may only shrink. An entry that no longer offends, or a count higher than the file's, fails too, so a PR that moves a page also takes it off the list.
- A method or local function counts as an event handler when its class subscribes it with `+=` (or wraps it in `new …EventHandler(…)`), or when an event attribute in its XAML names it (`Click="Save_Click"`, but not `x:Name="Save"`). A lambda counts as one when it is subscribed with `+=`. Any other async lambda that becomes a delegate returning `void`, such as an `Action`, counts as `async void`.
- The checks don't see the code that the XAML compiler and the MVVM Toolkit generate. Names that only that code defines stay unresolved, and no rule depends on them.

## Tech stack

| Package | Version | Used in | Purpose |
|---|---|---|---|
| Dapper | 2.1.72 | Engine | Micro-ORM |
| Microsoft.Data.SqlClient | 5.2.2 | Engine | SQL Server / Azure SQL driver |
| Microsoft.Data.Sqlite | 8.0.31 | Engine | History file |
| Microsoft.AspNetCore.OpenApi / Swashbuckle | 8.0.23 / 6.6.2 | Engine | Swagger (API mode only) |
| LiveChartsCore.SkiaSharpView.WPF | 2.0.0-rc4.5 | Desktop | Charts |
| System.Security.Cryptography.ProtectedData | 8.0.0 | Desktop | DPAPI |
| CommunityToolkit.Mvvm | 8.4.2 | Desktop | View models: `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` |
| Microsoft.Extensions.DependencyInjection | 8.0 (ASP.NET Core shared framework) | Desktop | The composition root (#182) |
| xunit | 2.9.3 | Tests | Unit tests |
| Microsoft.CodeAnalysis.CSharp | 4.14.0 | Desktop.Tests | The MVVM guard tests read the app's sources (#181) |
