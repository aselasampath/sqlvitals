# SqlVitals — WPF Desktop Application

A real-time SQL Server / Azure SQL monitoring desktop application built with **WPF (.NET 8)**.
Queries SQL Server DMVs directly — no separate server process, no HTTP round-trips.

Current version: **2.0**

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Solution Structure](#solution-structure)
4. [Tech Stack & NuGet Packages](#tech-stack--nuget-packages)
5. [Configuration](#configuration)
6. [How to Build & Run](#how-to-build--run)
7. [Navigation & Pages](#navigation--pages)
8. [Adding a New Page — Step-by-Step](#adding-a-new-page--step-by-step)
9. [Repository Pattern](#repository-pattern)
10. [SQL Files](#sql-files)
11. [Data Models](#data-models)
12. [Query Wrapper (NOLOCK + Timeout)](#query-wrapper-nolock--timeout)
13. [Azure SQL vs On-Premises Compatibility](#azure-sql-vs-on-premises-compatibility)
14. [Export / AI Report Feature](#export--ai-report-feature)
15. [Live SP Trace](#live-sp-trace)
16. [Styling & Themes](#styling--themes)
17. [Common Errors & Fixes](#common-errors--fixes)

---

## Overview

SqlVitals connects **directly** to a SQL Server or Azure SQL database and surfaces
live diagnostic data across the app's monitoring screens:

- Wait statistics (cumulative, active, categories, top types)
- TempDB pressure and file usage
- Memory grants and memory clerks
- Query Store top queries
- Index health (missing + unused indexes)
- Resource-intensive queries (reads + CPU)
- Index usage patterns and fragmentation
- Implicit type conversions
- Stale statistics
- Database storage, file sizes, and server configuration
- Export / AI report generator

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                  SqlVitals.Desktop (WPF)                   │
│                                                           │
│   MainWindow                                             │
│   ├── Sidebar navigation                                 │
│   ├── Frame → navigates to Page objects                  │
│   └── Refresh button → calls IRefreshable.RefreshAsync() │
│                                                           │
│   Pages                                                  │
│   └── Each page holds IWaitStatsRepository reference     │
│       and calls it in RefreshAsync()                     │
│                                                           │
│   ExportService                                          │
│   └── Fires all selected queries concurrently,           │
│       writes structured text report for AI pasting       │
└───────────────────────┬──────────────────────────────────┘
                        │  Project reference (no HTTP)
                        ▼
┌──────────────────────────────────────────────────────────┐
│         SqlVitals.Engine (Microsoft.NET.Sdk.Web library)   │
│                                                           │
│   Repositories/                                          │
│   ├── IWaitStatsRepository  (interface)                  │
│   └── WaitStatsRepository   (Dapper implementation)      │
│                                                           │
│   Models/    (C# record types, one file per feature area)│
│   Controllers/ + ApiHost.cs / Program.cs                 │
│   └── Optional standalone REST host — not used by Desktop│
└───────────────────────┬──────────────────────────────────┘
                        │  Dapper + Microsoft.Data.SqlClient
                        ▼
              SQL Server / Azure SQL Database
```

> **Key design decision:** `SqlVitals.Desktop` references `SqlVitals.Engine` as a
> **project reference**, not via HTTP. The repository is instantiated directly in
> `MainWindow`. This means zero network latency and no background web server needed.
> `SqlVitals.Engine` also contains a `Program.cs` / `ApiHost.cs` (and `Controllers/`) for
> running as a standalone REST API if needed in future, but the desktop app doesn't use it.
> Everything the app needs — models, repositories, and the optional REST surface — lives
> in this single project; there is no separate data-access project.

---

## Solution Structure

```
src/
├── SqlVitalsDashboard.slnx                 ← Solution file (open this in Visual Studio)
│
├── SqlVitals/
│   ├── Engine/                            ← Class library + optional web API
│   │   ├── SqlVitals.Engine.csproj
│   │   ├── appsettings.json               ← Connection string + query settings ← EDIT THIS
│   │   ├── Models/                        ← C# record types for every query result
│   │   │   ├── ActiveWait.cs
│   │   │   ├── DatabaseStorage.cs
│   │   │   ├── ImplicitConversion.cs
│   │   │   ├── IndexFragmentation.cs
│   │   │   ├── IndexHealth.cs
│   │   │   ├── IndexUsagePattern.cs
│   │   │   ├── MemoryGrant.cs
│   │   │   ├── PlanCacheData.cs
│   │   │   ├── QueryStoreData.cs
│   │   │   ├── ResourceIntensiveQuery.cs
│   │   │   ├── ServerHealthKpi.cs
│   │   │   ├── SignalVsResourceWait.cs
│   │   │   ├── StaleStatistic.cs
│   │   │   ├── TempDbPressure.cs
│   │   │   ├── TopWaitType.cs
│   │   │   └── WaitStatCumulative.cs
│   │   │       (+ more — see Models/ for the full current list)
│   │   ├── Repositories/
│   │   │   ├── IWaitStatsRepository.cs    ← Interface — defines the query methods
│   │   │   └── WaitStatsRepository.cs     ← Dapper implementation
│   │   ├── Controllers/
│   │   │   └── WaitStatsController.cs     ← REST endpoints (only used if running as API)
│   │   ├── Errors/
│   │   │   └── WaitStatsException.cs
│   │   ├── Services/                      ← (reserved for future engine-level business logic)
│   │   ├── Utilities/                     ← (reserved for future helpers)
│   │   ├── AnalysisEngine.cs              ← placeholder entry point for future analysis logic
│   │   ├── ApiHost.cs / Program.cs        ← Web API host (not used by desktop app)
│   │   └── wwwroot/                       ← Static assets for standalone API mode
│   │
│   └── Desktop/                           ← WPF application
│       ├── SqlVitals.Desktop.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── MainWindow.xaml                ← Shell: sidebar + frame + status bar
│       ├── MainWindow.xaml.cs             ← Navigation logic + Repo instantiation
│       ├── Pages/
│       │   ├── IRefreshable.cs            ← Interface every page must implement
│       │   └── ...Page.xaml/.cs           ← One file pair per monitoring screen
│       ├── Controls/
│       ├── Windows/
│       ├── Helpers/
│       ├── Services/
│       │   ├── ConnectionSettingsService.cs
│       │   └── ExportService.cs           ← Concurrent multi-group AI export
│       └── Styles/
│           └── (XAML resource dictionaries for dark theme)
```

---

## Tech Stack & NuGet Packages

| Package | Version | Used in | Purpose |
|---|---|---|---|
| `Dapper` | 2.1.72 | `SqlVitals.Engine` | Micro-ORM — maps SQL results to C# records |
| `Microsoft.Data.SqlClient` | 5.2.2 | `SqlVitals.Engine` | SQL Server / Azure SQL driver |
| `Microsoft.AspNetCore.OpenApi` | 8.0.23 | `SqlVitals.Engine` | Swagger (API mode only) |
| `Swashbuckle.AspNetCore` | 6.6.2 | `SqlVitals.Engine` | Swagger UI (API mode only) |
| `LiveChartsCore.SkiaSharpView.WPF` | 2.0.0-rc4.5 | `SqlVitals.Desktop` | Charts (bar, pie, line) |

**Target framework:** `net8.0-windows` (WPF) / `net8.0` (Engine, `Microsoft.NET.Sdk.Web`)

---

## Configuration

Edit **`src/SqlVitals/Engine/appsettings.json`** before running:

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=YOUR_SERVER;Database=YOUR_DB;User Id=USER;Password=PASS;TrustServerCertificate=false;"
  },
  "QuerySettings": {
    "CommandTimeoutSeconds": 30,
    "UseNoLock": true
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `ConnectionStrings:SqlServer` | *(must set)* | ADO.NET connection string for SQL Server or Azure SQL |
| `QuerySettings:CommandTimeoutSeconds` | `30` | Max seconds any query may run before being cancelled |
| `QuerySettings:UseNoLock` | `true` | Prepends `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED` to all queries (avoids blocking on monitored server) |

> **Azure SQL:** Use `TrustServerCertificate=false` and `Encrypt=true` (default).
> **On-premises:** Set `TrustServerCertificate=true` for self-signed certs.

The file is linked into `SqlVitals/Desktop/bin/Debug/net8.0-windows/appsettings.json`
automatically by the `.csproj` `<None Update>` entry — you only need to edit it once.

Connections entered in the app's **Settings** page are saved per-user (DPAPI-encrypted)
to `%AppData%\SqlVitals\settings.dat`, and the active one overrides the connection string from `appsettings.json`.

### Multiple connections

You can save any number of connections (e.g. Production, UAT, Dev) and switch between them with the
**Active connection** selector at the top of the sidebar. No restart is needed.

- **Save** checks the connection against the server, then adds or updates it. A connection that fails the check is not saved.
  **Save & Connect** also makes it the active connection.
- Switching reopens the current page against the new database. Loads still running against the previous
  database are discarded, so its data is never shown under the new connection.
- Passwords reach disk only when **Remember password** is ticked. Otherwise they are kept in memory for the
  current session, so you are asked once per app launch.
- Credentials are never shown in the connection list, the selector or error messages. The *Additional parameters*
  field rejects `Password`/`User ID`.
- Settings files from earlier versions (one connection) are migrated automatically to a single active connection.

---

## How to Build & Run

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 (WPF requires Windows)
- SQL Server or Azure SQL with `VIEW SERVER STATE` permission granted

### SQL permissions required

| Feature | On-premises / SQL MI | Azure SQL Database |
|---|---|---|
| Every page except SP Trace | `VIEW SERVER STATE` | `VIEW DATABASE STATE` |
| SP Trace — DMV fallback mode | `VIEW SERVER STATE` | `VIEW DATABASE STATE` |
| SP Trace — Extended Events mode | `VIEW SERVER STATE` **+** `ALTER ANY EVENT SESSION` | `VIEW DATABASE STATE` **+** `ALTER ANY DATABASE EVENT SESSION` |

**Minimum — everything except per-call tracing:**

```sql
-- On-premises / SQL Managed Instance (server-scoped)
GRANT VIEW SERVER STATE TO [your_login];

-- Azure SQL Database (database-scoped, run in the user database)
GRANT VIEW DATABASE STATE TO [your_user];
```

**To also get per-call SP tracing** (Extended Events). Without these the SP Trace page still
works — it drops to DMV fallback mode and says so — so grant them only if you want
individual call rows:

```sql
-- On-premises / SQL Managed Instance: server-level, granted in master
GRANT ALTER ANY EVENT SESSION TO [your_login];

-- Azure SQL Database: database-level, granted in the user database
GRANT ALTER ANY DATABASE EVENT SESSION TO [your_user];
```

> **What `ALTER ANY EVENT SESSION` allows.** It lets the login create, start, stop and drop
> *any* Extended Events session on that scope — not just this app's. On-premises that is a
> server-wide permission. If that is too broad for your environment, withhold it: DMV
> fallback mode needs nothing beyond `VIEW SERVER STATE` and still reports per-procedure
> executions, duration, CPU and reads.

**Verify what the current login has:**

```sql
-- On-premises
SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE')       AS CanReadDmvs,
       HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY EVENT SESSION') AS CanTrace;

-- Azure SQL Database
SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE')            AS CanReadDmvs,
       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER ANY DATABASE EVENT SESSION') AS CanTrace;
```

`SpTraceRepository.CanCreateEventSessionAsync` runs exactly this probe at trace start to
choose the capture mode.

### From PowerShell

```powershell
# From repo root — build and run
cd src
dotnet build SqlVitalsDashboard.slnx --nologo
Start-Process "SqlVitals\Desktop\bin\Debug\net8.0-windows\SqlVitals.Desktop.exe"
```

### Kill old instance + rebuild + relaunch (use this when already running)

```powershell
Get-Process -Name "SqlVitals.Desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
dotnet build "src\SqlVitals\Desktop\SqlVitals.Desktop.csproj" --nologo
Start-Process "src\SqlVitals\Desktop\bin\Debug\net8.0-windows\SqlVitals.Desktop.exe"
```

### Visual Studio

Open `src/SqlVitalsDashboard.slnx`, set `SqlVitals.Desktop` as startup project, press **F5**.

---

## Navigation & Pages

`MainWindow` hosts a left-side navigation sidebar. Each button has a `Tag` string that maps
to a page. Navigation is handled in `MainWindow.xaml.cs → NavigateTo(string tag)`.

| Nav Button | Tag string | Page class | Repository method(s) |
|---|---|---|---|
| Live Metrics | `LiveMetrics` | `LiveMetricsDashboardPage` | Live snapshot methods |
| Top Waits | `TopWaits` | `TopWaitsPage` | `GetTopWaitTypesAsync`, `GetCumulativeWaitsAsync` |
| Active Waits | `ActiveWaits` | `ActiveWaitsPage` | `GetActiveWaitsAsync` |
| Wait Trend | `WaitTrend` | `WaitStatsTrendPage` | Trend query methods |
| TempDB | `TempDb` | `TempDbPage` | `GetTempDbPressureAsync` |
| Memory Grants | `Memory` | `MemoryGrantsPage` | `GetMemoryGrantsAsync` |
| Query Store | `QueryStore` | `QueryStorePage` | `GetQueryStoreAsync` |
| Index Health | `IndexHealth` | `IndexHealthPage` | `GetIndexHealthAsync` |
| Resource Queries | `ResourceQueries` | `ResourceQueriesPage` | `GetResourceIntensiveQueriesAsync` |
| Implicit Conv. | `ImplicitConv` | `ImplicitConversionsPage` | `GetImplicitConversionsAsync` |
| Plan Cache Health | `PlanCacheHealth` | `PlanCacheHealthPage` | Plan cache health methods |
| Stale Stats | `StaleStats` | `StaleStatisticsPage` | `GetStaleStatisticsAsync` |
| DB Storage | `DbStorage` | `DatabaseStoragePage` | `GetDatabaseStorageAsync` |
| App Connections | `AppConnections` | `ApplicationConnectionsPage` | Application connection methods |
| Perfmon | `Perfmon` | `PerfmonPage` | Perfmon counter methods |
| SP Trace | `SpTrace` | `SpTracePage` | `StartTraceAsync`, `PollTraceEventsAsync`, `PollProcedureStatsAsync` |
| Export / AI | `Export` | `ExportPage` | *(all groups via ExportService)* |
| Settings | `Settings` | `SettingsPage` | *(connection settings only — no repository)* |

> The table above reflects the nav tags wired up in `MainWindow.xaml.cs`. See
> `SqlVitals/Engine/Repositories/IWaitStatsRepository.cs` for the full, current method list —
> it has grown well past the methods shown here as pages were added.

Every page implements `IRefreshable`:

```csharp
public interface IRefreshable
{
    Task RefreshAsync();
}
```

`MainWindow` calls `RefreshAsync()` once on navigate and again when the **Refresh** button
is clicked. Data errors are caught in `MainWindow.NavigateTo` and shown in a `MessageBox`.

---

## Adding a New Page — Step-by-Step

Follow this exact pattern every time you add a new monitoring screen.

### 1. Write the SQL file

Add `SQL/20_YourFeature.sql` in the `SQL/` folder at the repo root.
Keep it compatible with both Azure SQL and on-premises (see [compatibility notes](#azure-sql-vs-on-premises-compatibility)).

### 2. Create the C# model

Add `src/SqlVitals/Engine/Models/YourFeature.cs`:

```csharp
namespace SqlVitals.Engine.Models;

public record YourFeatureRow(
    string  SomeColumn,
    int     AnotherColumn,
    decimal ValueMB
);
```

Use `record` types — Dapper maps columns by name automatically (case-insensitive).

### 3. Add the interface method

In `src/SqlVitals/Engine/Repositories/IWaitStatsRepository.cs`:

```csharp
Task<IEnumerable<YourFeatureRow>> GetYourFeatureAsync();
```

### 4. Implement in the repository

In `WaitStatsRepository.cs`, add at the bottom before the closing `}`:

```csharp
// ── Your feature ──────────────────────────────────────────────────
public async Task<IEnumerable<YourFeatureRow>> GetYourFeatureAsync()
{
    const string sql = """
        SELECT
            some_column  AS SomeColumn,
            another_col  AS AnotherColumn,
            CAST(value_pages * 8.0/1024 AS DECIMAL(18,2)) AS ValueMB
        FROM sys.some_dmv
        ORDER BY ValueMB DESC
        """;

    using var conn = CreateConnection();
    return (await Q(conn, sql)).Select(r => new YourFeatureRow(
        (string)r.SomeColumn,
        Convert.ToInt32(r.AnotherColumn),
        Convert.ToDecimal(r.ValueMB)));
}
```

> **Always use `Q(conn, sql)`** — never `conn.QueryAsync` directly.
> **Always use `Convert.ToDecimal` / `Convert.ToInt32`** etc. for numeric fields —
> never `(decimal)r.Field` or `r.Field ?? 0m`. See [repository pattern](#repository-pattern).

### 5. Create the WPF page

`src/SqlVitals/Desktop/Pages/YourFeaturePage.xaml`:

```xml
<Page x:Class="SqlVitals.Desktop.Pages.YourFeaturePage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Your Feature" Style="{StaticResource PageTitle}" />

        <DataGrid x:Name="MainGrid" Grid.Row="1"
                  AutoGenerateColumns="False"
                  Style="{StaticResource DataGridStyle}">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Name"    Binding="{Binding SomeColumn}" Width="200"/>
                <DataGridTextColumn Header="Value MB" Binding="{Binding ValueMB, StringFormat=N2}" Width="100"/>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</Page>
```

`YourFeaturePage.xaml.cs`:

```csharp
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class YourFeaturePage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public YourFeaturePage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        var data = (await _repo.GetYourFeatureAsync()).ToList();
        MainGrid.ItemsSource = data;
    }
}
```

### 6. Wire up navigation in MainWindow

**`MainWindow.xaml`** — add nav button in the sidebar `StackPanel`:

```xml
<Button x:Name="BtnYourFeature" Tag="YourFeature"
        Content="&#xE8XX;  Your Feature"
        Style="{StaticResource NavButton}"
        Click="Nav_Click" />
```

**`MainWindow.xaml.cs`** — three places:

```csharp
// 1. Add to foreach reset array
foreach (var btn in new[] { ..., BtnYourFeature })
    btn.Style = (Style)FindResource("NavButton");

// 2. Add to 'active' switch
"YourFeature" => BtnYourFeature,

// 3. Add to 'page' switch
"YourFeature" => new YourFeaturePage(Repo),
```

### 7. (Optional) Add to Export

In `ExportService.cs`:

```csharp
public const string G_YOUR_FEATURE = "Your Feature";

public static readonly string[] AllGroups = [ ..., G_YOUR_FEATURE ];

// In ExportAsync():
if (groups.Contains(G_YOUR_FEATURE)) tasks.Add((G_YOUR_FEATURE, FetchYourFeature()));

private async Task<string> FetchYourFeature()
{
    var rows = (await repo.GetYourFeatureAsync()).ToList();
    var sb = new StringBuilder();
    sb.AppendLine($"## Your Feature ({rows.Count} rows)");
    foreach (var r in rows)
        sb.AppendLine($"  {r.SomeColumn} | {r.ValueMB:N2} MB");
    return sb.ToString();
}
```

Add a description entry in `ExportPage.xaml.cs` in the `_descriptions` dictionary.

---

## Repository Pattern

`WaitStatsRepository` (in `SqlVitals.Engine.Repositories`) is the single class that executes
all SQL against the database.

### Connection

```csharp
private IDbConnection CreateConnection() =>
    new SqlConnection(configuration.GetConnectionString("SqlServer"));
```

A **new connection is opened per method call** (`using var conn = CreateConnection()`).
Dapper opens it lazily on the first query.

### Query wrappers

```csharp
// Multiple rows — returns IEnumerable<dynamic>
private async Task<IEnumerable<dynamic>> Q(IDbConnection conn, string sql, object? param = null)

// Zero or one row — returns dynamic? (null if no rows)
private async Task<dynamic?> QFirst(IDbConnection conn, string sql, object? param = null)
```

Both prepend `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;` when `UseNoLock: true`
and pass `commandTimeout: _timeoutSec` to every query.

### Null-safe casting rule

Dapper `dynamic` rows return `DBNull.Value` (not C# `null`) for SQL `NULL` columns.
The `?? 0m` operator does **not** intercept `DBNull`. Always use `Convert.To*`:

```csharp
// ✅ Correct
Convert.ToDecimal(r.FileSizeMB)       // returns 0m if DBNull
Convert.ToInt32(r.RowsCount)          // returns 0 if DBNull
Convert.ToInt64(r.CurrentValue)       // returns 0L if DBNull
Convert.ToBoolean(r.IsEnabled)        // returns false if DBNull

// For nullable strings:
r.PhysicalPath is DBNull ? "" : (string)r.PhysicalPath

// ❌ Wrong — throws InvalidCastException on DBNull
(decimal)r.FileSizeMB
(int)r.RowsCount
r.FileSizeMB ?? 0m
```

---

## SQL Files

Reference SQL files live in `SQL/` at the repo root. The actual queries are embedded
as C# raw string literals inside `WaitStatsRepository.cs` (not loaded from files at
runtime). The `.sql` files serve as human-readable documentation and can be run
directly in SSMS or Azure Data Studio for testing.

| File | Feature | Key DMVs |
|---|---|---|
| `01_WaitStats_Cumulative.sql` | All cumulative waits | `sys.dm_os_wait_stats` |
| `02_ActiveWaits.sql` | Live waiting sessions | `sys.dm_exec_requests`, `sys.dm_exec_sessions` |
| `03_WaitCategorySummary.sql` | Category aggregation | `sys.dm_os_wait_stats` |
| `04_SignalVsResourceWaits.sql` | CPU pressure signal | `sys.dm_os_wait_stats` |
| `05_TopWaitTypes.sql` | Top 25 waits | `sys.dm_os_wait_stats` |
| `07_ServerHealthKPIs.sql` | Header KPIs | `sys.dm_os_sys_info`, `sys.dm_os_performance_counters` |
| `08_TempDB_Pressure.sql` | TempDB file + session usage | `sys.dm_db_task_space_usage` |
| `09_MemoryGrants.sql` | Memory grant waits | `sys.dm_exec_query_memory_grants` |
| `10_QueryStore_TopQueries.sql` | Query Store analysis | `sys.query_store_*` |
| `11_IndexHealth.sql` | Missing + unused indexes | `sys.dm_db_missing_index_*`, `sys.dm_db_index_usage_stats` |
| `12_PlanCache_Pressure.sql` | Plan cache analysis | `sys.dm_exec_cached_plans` |
| `13_TopQueries_LogicalReads.sql` | Top reads queries | `sys.dm_exec_query_stats` |
| `14_TopQueries_CPU.sql` | Top CPU queries | `sys.dm_exec_query_stats` |
| `15_IndexUsagePatterns.sql` | Index seek/scan/lookup ratios | `sys.dm_db_index_usage_stats` |
| `16_IndexFragmentation.sql` | Fragmentation % | `sys.dm_db_index_physical_stats` |
| `17_ImplicitConversions.sql` | Type conversion warnings | `sys.dm_exec_query_stats` + plan XML |
| `18_StaleStatistics.sql` | Out-of-date statistics | `sys.stats`, `sys.dm_db_stats_properties` |
| `19_DatabaseStorage.sql` | DB files, TempDB files, config | `sys.database_files`, `sys.configurations` |

> This list reflects the original feature set. Several pages (Live Metrics, Perfmon,
> Application Connections, Plan Cache Health, Wait Trend) were added afterward — see
> `SqlVitals/Engine/Repositories/WaitStatsRepository.cs` for their queries.

---

## Data Models

All models are C# `record` types in `SqlVitals.Engine.Models`. Dapper maps SQL column
aliases to record constructor parameters by name (case-insensitive). See the
`Models/` folder in [Solution Structure](#solution-structure) for the current full list.

---

## Query Wrapper (NOLOCK + Timeout)

All queries go through `Q()` or `QFirst()` at the top of `WaitStatsRepository.cs`:

```csharp
private async Task<IEnumerable<dynamic>> Q(IDbConnection conn, string sql, object? param = null)
{
    var effectiveSql = _useNoLock
        ? "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n" + sql
        : sql;
    return await conn.QueryAsync(effectiveSql, param, commandTimeout: _timeoutSec);
}
```

Settings read once at startup from `appsettings.json`:

```csharp
private readonly int  _timeoutSec = configuration.GetValue<int> ("QuerySettings:CommandTimeoutSeconds", 30);
private readonly bool _useNoLock  = configuration.GetValue<bool>("QuerySettings:UseNoLock", true);
```

This applies NOLOCK-equivalent behaviour **globally** without touching individual SQL
strings — flip `UseNoLock: false` in config to disable it for all queries at once.

---

## Azure SQL vs On-Premises Compatibility

Several server-level DMVs are **not available on Azure SQL Database** (EngineEdition = 5):

| View | On-Prem | Azure SQL DB | Azure SQL MI |
|---|---|---|---|
| `sys.master_files` | ✅ | ❌ | ✅ |
| `tempdb.sys.database_files` | ✅ | ❌ (cross-DB blocked) | ✅ |
| `sys.configurations` | ✅ | ❌ | ✅ |
| `sys.database_files` | ✅ (current DB) | ✅ | ✅ |
| `sys.database_scoped_configurations` | ✅ | ✅ | ✅ |
| `sys.dm_db_file_space_usage` | ✅ | ✅ | ✅ |

`GetDatabaseStorageAsync()` detects the edition at runtime and switches queries:

```csharp
const string sqlEdition = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition";
var editionRow = await QFirst(conn, sqlEdition);
bool isAzureSqlDb = Convert.ToInt32(editionRow.Edition) == 5;

// Then use:
var filesSql  = isAzureSqlDb ? sqlFilesAzure  : sqlFilesOnPrem;
var configSql = isAzureSqlDb ? sqlConfigAzure : sqlConfigOnPrem;
// TempDB query is skipped entirely on Azure SQL DB
if (!isAzureSqlDb) { /* fetch tempdb files */ }
```

When adding new pages that query server-level views, add the same pattern.

**Handling `sql_variant` columns** (e.g., `sys.database_scoped_configurations.value`
contains a mix of numeric and string values):

```sql
CASE
    WHEN ISNUMERIC(CAST(value AS NVARCHAR(256))) = 1
    THEN CAST(CAST(value AS NVARCHAR(256)) AS BIGINT)
    ELSE 0
END AS CurrentValue,
CAST(ISNULL(CAST(value AS NVARCHAR(256)),'') AS NVARCHAR(MAX)) AS Description
```

**Avoid reserved T-SQL keywords as column aliases:**
`ROWCOUNT`, `ROWS`, `TABLE`, `KEY`, `INDEX`, `VALUE` — use `RowsCount`, `RowCount_` etc.

---

## Export / AI Report Feature

`ExportPage` + `ExportService` let users select any combination of data groups,
fetch them all **concurrently**, and write a structured plain-text file (default name
`SqlVitals_Report.txt`) for pasting into an AI assistant.

### How it works

1. User ticks checkboxes in `ExportPage`
2. Clicks **Export** → `ExportService.ExportAsync(selectedGroups, filePath)`
3. `ExportAsync` builds `List<(string Group, Task<string> Work)>` and fires them with
   `await Task.WhenAll(...)` — all selected groups fetch in parallel
4. Results assembled in the order of `AllGroups` (stable, predictable output)
5. Written to the chosen file path as UTF-8 plain text

### Group constants

```csharp
public const string G_TOP_WAITS        = "Top Wait Types";
public const string G_CATEGORIES       = "Wait Category Summary";
public const string G_ACTIVE_WAITS     = "Active Waits (live)";
public const string G_SIGNAL_VS_RES    = "Signal vs Resource Wait";
public const string G_TEMPDB           = "TempDB Pressure";
public const string G_MEMORY_GRANTS    = "Memory Grants";
public const string G_QUERY_STORE      = "Query Store Health";
public const string G_INDEX_HEALTH     = "Missing / Unused Indexes";
public const string G_RESOURCE_QUERIES = "Resource-Intensive Queries";
public const string G_INDEX_USAGE      = "Index Usage Patterns";
public const string G_INDEX_FRAG       = "Index Fragmentation";
public const string G_IMPLICIT_CONV    = "Implicit Conversions";
public const string G_STALE_STATS      = "Stale Statistics";
public const string G_DB_STORAGE       = "Database Storage & Configuration";
public const string G_SP_TRACE         = "Stored Procedure Activity";
```

---

## Live SP Trace

`SpTracePage` streams stored-procedure calls as they happen. It has two capture modes and
picks between them automatically when you press **Start Trace**.

| Mode | Source | Requires | Gives |
|---|---|---|---|
| **Extended Events** | app-managed XE session, `ring_buffer` target | `ALTER ANY EVENT SESSION` (on-prem) / `ALTER ANY DATABASE EVENT SESSION` (Azure SQL DB) | per-call rows: procedure, duration, CPU, reads/writes, rows, SPID, login, app, host, statement |
| **DMV fallback** | `sys.dm_exec_procedure_stats` snapshot deltas | nothing beyond `VIEW SERVER STATE` | per-procedure aggregates for the poll window |

The mode badge in the status strip shows which is active, and the DMV fallback always
explains *why* it was chosen. A missing permission never breaks the page.

See [SQL permissions required](#sql-permissions-required) for the exact `GRANT` statements.
In short: `ALTER ANY EVENT SESSION` (on-prem) or `ALTER ANY DATABASE EVENT SESSION` (Azure
SQL DB) buys you per-call rows; without it you still get per-procedure aggregates.

### Event session lifecycle

The session is named from `TraceSettings:SessionName` (default `SqlVitals_SpTrace`) and is
**created on Start and dropped on Stop**. It is also dropped when you navigate away from
the page and when the app closes (`MainWindow.OnClosing`), and any session left behind by a
crash is dropped by name before the next one is created. Nothing is left running on the
monitored server.

> `MainWindow.OnClosing` blocks for up to 20 s on the drop and first calls
> `SpTracePage.PrepareForShutdown()` to stop the poll timer. Without that the in-flight poll
> holds the repository's gate and the drop misses its window — which leaves a live session
> on the server.

### Compatibility notes

- **Both events are captured.** `CommandType.StoredProcedure` calls arrive as
  `rpc_completed`; `EXEC dbo.usp_X` sent as a batch arrives as `module_end`. Capturing only
  one silently misses half the traffic depending on how callers are written.
- **`object_type = 8272`** narrows `module_end` to stored procedures, but **Azure SQL
  Database rejects that predicate**. `CreateAndStartSessionAsync` retries without it and
  filters functions/triggers client-side via `SpTraceEvent.IsStoredProcedure`.
- Azure SQL Database uses a database-scoped session (`ON DATABASE`) and reads
  `sys.dm_xe_database_sessions` / `sys.dm_xe_database_session_targets`; on-premises uses the
  server-scoped equivalents. Branching follows the existing `IsAzureSqlDatabaseAsync()`
  pattern.

### Overhead

Tracing a busy server is not free. The trace never auto-starts, filters to the connected
database, caps the ring buffer (`max_memory` 4 MB, `max_events_limit` 1000) and exposes a
**Min duration (ms)** filter — raise it on a hot server. Dropped-event and truncation
warnings are surfaced in the status strip rather than silently making the server look quiet.

### Pure, testable helpers

`SpTraceXmlParser` (ring-buffer XML → events) and `ProcedureStatsDelta` (two DMV snapshots
→ per-window activity) are static and database-free, and carry the bulk of the unit tests.
XML shredding is done in C# rather than with T-SQL `.nodes()` so the monitored server does
not spend CPU on it.

---

## Styling & Themes

The app uses a **dark theme** defined in XAML resource dictionaries under `SqlVitals/Desktop/Styles/`.

Key style resource keys used across pages:

| Style key | Element | Description |
|---|---|---|
| `NavButton` | Sidebar `Button` | Default grey nav button |
| `NavButtonActive` | Sidebar `Button` | Active page — purple highlight |
| `PageTitle` | `TextBlock` | Large white page heading |
| `KpiCard` | `Border` | Dark rounded card for metric tiles |
| `KpiValue` | `TextBlock` | Large metric number |
| `KpiLabel` | `TextBlock` | Small grey subtitle |
| `DataGridStyle` | `DataGrid` | Dark grid with alternating rows |
| `SubTabButton` | `Button` | Sub-tab toggle (used in DB Storage) |
| `SubTabButtonActive` | `Button` | Active sub-tab |

Colour palette reference (use in new pages):

```
Background:   #12121E    Surface:    #1E1E2E    Border:    #2D2D44
Text:         #E2E8F0    Muted text: #94A3B8
Accent blue:  #38BDF8    Accent purple: #A855F7
Success:      #10B981    Warning:    #F59E0B    Error:     #EF4444
```

For LiveCharts (SkiaSharp colours):
```csharp
var textColor = SKColor.Parse("#94A3B8");
var gridColor = SKColor.Parse("#2D2D44");
var accent    = SKColor.Parse("#A855F7");
```

---

## Common Errors & Fixes

### Build error: "file locked by SqlVitals.Desktop process"

The app is already running. Kill it before rebuilding:

```powershell
Get-Process -Name "SqlVitals.Desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
```

### Runtime: "Invalid object name 'sys.master_files'"

Connected to **Azure SQL Database**. `GetDatabaseStorageAsync` detects this automatically.
If a different page throws this, add the `isAzureSqlDb` branching (see above).

### Runtime: "Cannot convert null to 'decimal'"

Dapper returns `DBNull.Value` for SQL NULLs on `dynamic` rows. `?? 0m` doesn't help.
Fix: use `Convert.ToDecimal(r.Col)` everywhere.

### Runtime: "Error converting data type nvarchar to bigint"

A `sql_variant` column contains string values. Use the `ISNUMERIC` CASE pattern.

### Runtime: "Incorrect syntax near 'RowCount'" / "'THEN'"

- `ROWCOUNT` is a reserved keyword — rename the alias.
- `CASE WHEN EXISTS (subquery)` in GROUP BY context — replace with `LEFT JOIN` + `MAX(CASE ...)`.

### Runtime: "Incorrect syntax near 'THEN'" in ORDER BY

Column aliases are not always resolvable in `ORDER BY` after `GROUP BY` on Azure SQL.
Use the full expression: `ORDER BY SUM(a.total_pages) DESC` instead of `ORDER BY TotalSizeMB DESC`.

### SP Trace: "Login lacks ALTER ANY EVENT SESSION"

Expected on a locked-down login. The page drops to DMV fallback mode and keeps working with
per-procedure aggregates. Grant the permission (see
[SQL permissions required](#sql-permissions-required)) if you want per-call rows.

### SP Trace: 'The value specified for ... "object_type", event, "module_end", is invalid'

Azure SQL Database rejects the `object_type = 8272` predicate that narrows `module_end` to
stored procedures. `CreateAndStartSessionAsync` already handles this — it retries without
the predicate and filters client-side. If you see this surface as a *fallback reason*, the
retry itself failed, which points at something else in the DDL.

### SP Trace: an event session is left behind on the server

Should not happen — it is dropped on Stop, on navigating away, and on app close. If a crash
or a killed process leaves one, the next **Start Trace** drops it by name first. To remove it
by hand:

```sql
-- On-premises
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = 'SqlVitals_SpTrace')
    DROP EVENT SESSION [SqlVitals_SpTrace] ON SERVER;

-- Azure SQL Database
IF EXISTS (SELECT 1 FROM sys.database_event_sessions WHERE name = 'SqlVitals_SpTrace')
    DROP EVENT SESSION [SqlVitals_SpTrace] ON DATABASE;
```

### IDE shows "type not found" errors for Models

False-positive from stale Roslyn/IntelliSense cache. The `using SqlVitals.Engine.Models`
is already present. Run an actual `dotnet build` to confirm — it will show `0 Error(s)`.
