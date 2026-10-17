# Architecture

*For developers and contributors.* How SqlVitals is put together.

## The big picture

SqlVitals is a **WPF desktop app (.NET 10)** that queries SQL Server's DMVs **directly**. There is no web server or HTTP hop: the app references the Engine library and calls it in-process.

![Desktop and the Collector service use the Engine library, which reads SQL Server, writes history.db and posts to Slack or Teams](images/architecture/big-picture.png)

## Projects

| Project | Role |
|---|---|
| `SqlVitals.Desktop` | The app users run (`net10.0-windows`, WPF). Its `<Version>` is the release number. |
| `SqlVitals.Engine` | Models, repositories, monitoring, verdicts, history, scripting, export (`net10.0` class library). |
| `SqlVitals.Collector` | The optional [collector service](Collector-Service.md): Engine's monitoring as a Windows service. |
| `SqlVitals.Installer` | The Setup wizard (`net472`, so it runs on a clean Windows). |
| `*.Tests` | xUnit tests for each project. `Engine.IntegrationTests` runs every query against real SQL Servers in CI; see [Building and Contributing](Building-and-Contributing.md#against-a-real-sql-server). |

## The window

![The main window: sidebar, top bar, the page on PageShell (verdict, folding sections, detail) and the details drawer](images/architecture/window.png)

- **Opening a page:** every route goes through `ShellViewModel.OpenAsync(route)`. `Services/PageFactory` builds the page fresh with its view model, and the window puts it on screen; Back and Forward come from `NavigationHistory`, and `Helpers/ViewState` puts back the tab, sort, filter, selection and scroll. See [The shell](#the-shell).
- **The top bar** shows what the page reports through `IPageContext` (its kind, interval, pause) and `IShellStatus` (read times, errors).
- **Every page** but All servers, Overview, Live charts and Settings uses `Controls/PageShell`: a verdict first (a pure function in `Engine/Verdicts`), then sections that fold, then the detail.
- **The details drawer** shows the selected row of a page that implements `IDetailsSource`.

## Main flows

- **Reading a page:** the page's view model calls a repository in `LoadAsync`; results are records from `Engine/Models`, bound to grids and charts. A failure is reported once, in the verdict and the top bar.
- **Switching connections:** `IRepositoryFactory` builds the connection's repositories from its `SqlTarget` (connection string, timeout, settings), and the shell reopens the current page with them.
- **Monitoring:** `MonitoringManager` runs one Engine `Collector` per monitored connection, off the UI thread. Each sample is graded (`HealthRules`), scored (`HealthScore`), checked for alerts (`AlertTracker`) and written to the history (`HistoryWriter` → SQLite). See [Health Dot and Background Monitoring](Health-Dot-and-Background-Monitoring.md).
- **Notifications:** alerts go to Windows notifications (`TrayIcon`) and to Slack or Teams (`ChatNotificationService`).
- **History:** pages read past ranges through `IHistoryReader`; see [Monitoring History Database](Monitoring-History-Database.md).

## Data access conventions

- Every repository derives from `BaseRepository` and runs SQL only through its `Q`, `QFirst` and `Exec` helpers, never `conn.QueryAsync` directly. They apply the timeout, the isolation level and `LOCK_TIMEOUT`, and wrap failures in `WaitStatsException`, which says what failed and the `GRANT` that fixes a missing permission.
- Every repository method takes a `CancellationToken` and passes it on. A page's reads are cancelled when the page is left.
- `IWaitStatsRepository` is the sum of the area interfaces (`IWaitsRepository`, `IMemoryRepository`, …). A view model takes only the area interface it needs.

### Where the SQL lives

Every query is a `.sql` file under `Engine/Sql/<Area>/`, embedded in the assembly and read with `SqlScripts.Get("Waits/ActiveWaits")`. Open the file in SSMS to try it by hand.

- A query that changes with the server's version or edition has `/*NAME*/` markers the repository fills in.
- `SqlScriptsTests` fails for a missing or unused file; `SqlParseTests` parses every file (and every marker combination) with ScriptDom.

### DBNull-safe mapping

Dapper `dynamic` rows return `DBNull.Value` for SQL `NULL`, which `??` doesn't catch:

```csharp
Convert.ToDecimal(r.FileSizeMB)                          // ✅ 0m on DBNull
r.PhysicalPath is DBNull ? "" : (string)r.PhysicalPath   // ✅ nullable strings

(decimal)r.FileSizeMB                                    // ❌ InvalidCastException on DBNull
r.FileSizeMB ?? 0m                                       // ❌
```

### Azure SQL Database branching

Server-level views (`sys.master_files`, `sys.configurations`, …) don't exist on Azure SQL Database. Ask the connection's capabilities, read once per connection, and switch queries:

```csharp
var capabilities = await CapabilitiesAsync(cancellationToken);
var sql = capabilities.IsAzureSqlDatabase ? AzureSql : OnPremSql;
```

Pages with nothing to show there are listed in `ShellViewModel.PagesAzureSqlLacks` and disabled in the sidebar.

## UI conventions

- **Themes:** `Styles/LightTheme.xaml` and `DarkTheme.xaml`. Use **brush keys**, never hex colours (`BgCard`, `Accent`, `TextPrimary`, `Success`, `Warning`, `Danger`, …). A new colour goes in **both** themes.
- In code, take a brush with `SetResourceReference` so it follows a theme switch. `ColourGuardTests` fails on a hex colour in code or a key missing from a theme.
- Charts take their colours from `Helpers/ChartTheme`.

## MVVM

Each page, the window around them and each of the app's other windows is a XAML view with a view model that reads its data, decides what it shows and owns its refresh. `MvvmGuardTests` checks the rules below.

![A page's view binds to its view model, which alone calls a repository, which runs its SQL on the server](images/architecture/page-reads.png)

- **View:** `Pages/XPage.xaml` + code-behind with view code only: focus, keyboard, scrolling, the details drawer, drawing charts in theme colours.
- **View model:** `ViewModels/Pages/XViewModel.cs`, derived from `PageViewModel` (CommunityToolkit.Mvvm). It may use Engine, the service interfaces in `Services/`, and LiveChartsCore types, but not WPF controls, `MessageBox`, `Application` or a `Dispatcher`.
- **Buttons** bind `Command=` to `[RelayCommand]` methods, and `async void` is only for event handlers.
- **Services** a view model asks for instead of WPF: `IDialogService`, `IClipboard`, `IFileDialogs`, `INavigator`, `IThemeService`, `IShellStatus`, `IRefreshScheduler`, `IUiDispatcher`, with WPF implementations in `Services/Wpf/` and fakes in `Desktop.Tests/Fakes/`.
- **The guard tests fail** on a page that reads in its code-behind, a `Click=` in a page's, the shell's or a window's XAML, and a reference to `MainWindow` anywhere but `MainWindow` and `App`.

### The composition root

`Services/CompositionRoot` builds one dependency-injection container (singletons) at startup: logging, the UI services, the app services (settings, repositories factory, monitoring, history, chat), the pages and the shell's view model. `App` adds the main window, so nothing else refers to it. A new service is an interface in `Services/`, a line in the matching `Add…` method, and a fake for tests.

### The shell

The window's logic is `ViewModels/Shell/ShellViewModel`, the window's `DataContext`, with its parts as properties:

| View model | What it holds | Drawn by |
|---|---|---|
| `ShellViewModel` | The saved servers and the active one with its repositories, the page on screen (from the page factory), Back and Forward, the breadcrumb, what the page reports (`IShellStatus` goes here), the server workspace and the pages Azure lacks, focus mode, the alerts for the tray, and monitoring, the chat channels and the collector service, started and stopped with the window | `MainWindow` |
| `TopBarViewModel` | Back and Forward with where they go, the breadcrumb, the page's kind, freshness and database, Pause and Refresh, the message, ☰'s words, Focus and the details button, as commands | `Controls/TopBar` |
| `SidebarViewModel` | Pinned, collapsed or hidden (`SidebarLayout`), slid in over the page, the open group, the server header, the page highlighted, the alerts' count and the pages the server hasn't got | `Controls/WorkspaceShell` |
| `QuickSwitcherViewModel` | What is typed, the results in groups, the one selected, and opening it | `Controls/QuickSwitcherOverlay` |

- **The page itself stays in the window.** A view model never holds a `Page`, so the window is the shell's `Services/IPageHost`: `Show` puts a page from the factory in the frame, moves its controls to the top bar, applies its filter as evidence and points the details drawer at it, and returns what the page says about itself (`IPageContext`, `IFocusPage`); `KeepViewState` and `RestoreViewState` keep its sort, filter and scroll by route.
- **What needs a window stays in `MainWindow`:** the tray icon, the window's placement and focus mode's full screen, coming back when a second copy starts (`ShowFromTray`, which `App` calls), and the keys that work anywhere in it (Ctrl+K, Ctrl+B, F5, F11, Ctrl+J, Esc, Back and Forward), which call the shell.
- **The controls keep layout, pointer and keyboard.** The top bar giving way in a narrow window, the sidebar's hover delays, slide animation and where the keyboard goes, and the switcher keeping the keyboard in its box are the controls' own; what they show is their view model's, and their buttons are its commands.
- **The SQL script, query plan and Query Store query windows** have view models in `ViewModels/Windows/`, made by `IDialogService` as it opens them. What only the window can do is handed to it: the plan's diagram rendered to a PNG (`QueryPlanViewModel.RenderDiagram`), the Query Store query's chart drawn in theme colours from a `TrendChart`.
- **`INavigator` and `IShellStatus`** reach the shell in the container once a window has attached it (`ShellViewModel.IsAttached`), so before the window opens, or in a test without one, they do nothing.
- **The controls take their view model** through a `ViewModel` property, set once by the window; they have no events or methods that only pass through to it, so what drives them is the view model.

### The page lifecycle

`PageViewModel` runs every page the same way:

| Member | What it does |
|---|---|
| `LoadAsync(CancellationToken)` | Reads what the page shows; cancelled when the page is left |
| `OnNavigatedToAsync()` / `OnNavigatedFrom()` | The shell calls them when the page is shown and left |
| `RefreshCommand`, `RereadAsync()` | Read again; never two reads at once |
| `RefreshInterval`, `SetPaused(bool)`, `Countdown` | The page's own timer, the top bar's Pause / Resume, and when it reads next |
| `Verdict`, `ShowError` | What the page says; a failed read says what couldn't be read |

### Adding a page

1. **View:** `Pages/XPage.xaml(.cs)`, implementing `IPageContext`. The constructor takes the view model, sets it as `DataContext` before `InitializeComponent`, then calls `ViewModel.Attach(Shell)`.
2. **View model:** `ViewModels/Pages/XViewModel.cs`, overriding `LoadAsync`, taking the narrowest repository interface it needs.
3. **Catalog:** the tag in `Services/PageCatalog` and `Services/NavMenu`.
4. **Registration:** a line in `Services/PageRegistrations.AddPages`.

### Grids

- A grid binds `ItemsSource` to a `ViewModels/GridRows<T>` that the view model refills with `Replace(rows)`, so the user's sort and filter survive a refresh.
- The view model owns the selection (`SelectedItem` bound two way) and keeps it across a refresh by the row's key.

### Pages that follow the monitoring

- **A view model says what something means; the view colours it.** Home's tiles, the Overview's alerts, costs, headlines and *Since you last looked*, and Live charts' score carry a `HealthLevel` or `TileStatus`, not a brush: the XAML takes the colour through `Helpers/HealthBrush` (`ConverterParameter=Tint` for a status's wash), with a `FallbackValue` where nothing is graded (a headline with no threshold keeps the text's colour). A KPI card that is just information is `KpiCard.Tones.Muted`.
- **What the monitoring says is followed while the page is shown** (`OnShowing` to `OnLeaving`): a session's samples and state, monitoring started or stopped, alerts changed, settings saved (told on another thread, so posted with `IUiDispatcher`). A sample that moves a page without a read of its own reports `PageViewModel.ReportUpdated`, so the top bar's *As of* moves; an alert change reads again soon (`RefreshSoon`). These view models take `MonitoringManager` itself, and their tests run it over fake servers on the test's own thread with a dispatcher (`OnDispatcher.Run`, no STA, no page), as the app's sessions hand their samples to the UI thread.
- **Links the shell opens stay events.** Home's *Open*, *Investigate*, *Edit connection* and the Overview's *Make active*, *Change* thresholds and evidence routes open Settings, Alerts or a route with a time window, which `INavigator` doesn't: the view model raises the event, `ShellViewModel.Wire` handles it, and the page forwards it for its own tests (`add => ViewModel.OpenRequested += value`).
- **A menu or a popup is the view's; what it holds is the view model's.** Home's ⋯ raises `MenuRequested`; the page builds the `ContextMenu` from `MenuFor(tile)` beside the tile's button, and opens the score's breakdown when `Breakdown` is set. A control the page hands to the top bar (`TakeBarTools`) keeps its own `DataContext`, as Live charts' sample interval does, since it leaves the page's. An item that isn't a tile in a list of tiles (Home's *+ Add a connection*) is drawn by a `DataTemplateSelector`, not put in the list as a `Button`: WPF ignores the item template for it (binding error 26).
- **Query Store reads a tab when it is opened,** and again after Refresh or a new period. A tab's failure is said in its own line (`ScriptNotice` binds `Message` and `Script`), and the settings read is the only one that fails the page. The drawer shows the row picked last on any tab: a read selects its tab's row again, but moves the drawer only if it showed that query. Its plans open through `Services/QueryStorePlans`, shared with a query's window (`Helpers/BusyButton` greys the drawer's button meanwhile; the window's View Plan buttons are greyed by their command).

### Forms that save

- **Each category of Settings has a view model,** `ViewModels/Settings/XViewModel.cs` for `Pages/Settings/X.xaml`, derived from `SettingsCategoryViewModel` (`TryLeaveAsync`, `OnShownAgain`, and `Activate`/`Deactivate`, which the view calls as it is loaded and unloaded). `SettingsViewModel` keeps them while Settings is open, asks the one on screen before leaving it, and the page maps each to its view, made once. What they need from the app is `Services/SettingsContext`, with fakes for Test connection, the database list, the grid density and the log folder.
- **A form's rules are validation attributes** on an `ObservableValidator` (the connection editor): an error is shown under a field once it has been typed in (its `Tag` gives the red border, not WPF's validation state, so a new form isn't red all over), the first error in the save bar, and **Save** is disabled while there is any.
- **A switch that saves as it's clicked** binds `IsChecked` one way and a command that toggles the setting; the view model raises the property again when the save fails, so the switch goes back. A drop-down that saves as it's picked does the same, raised later through `IUiDispatcher.Post`, since a ComboBox ignores it during its own change.
- **A status line** is a `ViewModels/StatusLine` (redacted, ✓ or ✗) shown by `Controls/StatusText` with the `Status` style. A view model's secret (a password, a webhook URL) is in no bound property until the user asks for it: the PasswordBox's code-behind copies it into the view model, and a saved webhook URL stays out of the form until **Show**.
- **Two radio buttons that belong together are siblings without a `GroupName`:** a name groups them across the whole window, so another view's buttons (a page kept by the history, a test's) uncheck them through their bindings.

### Testing a page

- **The view model:** a plain xUnit test with `Fakes/FakeRepository`, no UI thread: loaded, empty, error, cancelled on leave, selection kept.
- **The page:** rendered off screen in both themes inside `BindingErrors.Watch()`, which fails on any broken binding.
- **The shell:** `ShellViewModelTests` drive `ShellViewModel` over saved connections with `Fakes/FakePageHost` for the window and a factory of stub view models, no window and no page: switching servers, Back and Forward, the pages Azure lacks, the top bar, the sidebar and the quick switcher. The windows are built over their view models in both themes, as pages are.

## Tech stack

| Package | Used for |
|---|---|
| Dapper, Microsoft.Data.SqlClient | Querying SQL Server and Azure SQL (Entra MFA through `Microsoft.Data.SqlClient.Extensions.Azure`) |
| Microsoft.Data.Sqlite | The history file |
| LiveChartsCore.SkiaSharpView.WPF | Charts |
| CommunityToolkit.Mvvm | View models |
| Microsoft.Extensions.DependencyInjection, .Configuration, .Logging | The composition root, `appsettings.json`, the log file |
| Microsoft.SqlServer.TransactSql.ScriptDom | Removing literals from query text; parsing every query in tests |
| xunit | Tests |
