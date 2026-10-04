# Server Impact and Permissions

*For DBAs and anyone approving SqlVitals for use against production.* It covers what SqlVitals runs on your server, how often, with which permissions, and what it never does.

## Summary

- **Read-only.** SqlVitals reads DMVs and catalog views. It creates no objects, jobs, tables or agents on the server. (DB Storage collects each database's used space in a `#temporary` table that lives only for the one batch.)
- **One exception:** [SP Trace](SP-Trace.md) creates a temporary Extended Events session **only when a user presses Start**, and drops it on Stop, on leaving the page, and when the app exits.
- **Never runs generated scripts.** Index and statistics scripts are shown for review only.
- **Never blocks.** Every query runs under `READ UNCOMMITTED` with a command timeout (30 s by default).
- **Nothing runs by default.** From 0.46.0 a new installation monitors no server until the user presses **Start** or turns on **Start monitoring automatically**. Until then the only queries are the ones of the pages the user opens, plus the Azure SQL Database edition check on connecting. See [below](#what-runs-by-default).
- **Home adds nothing on the server.** [All servers](Your-Servers.md) (Home, from 0.53.0) shows what monitoring has already collected and the local history. A server's details page reads the sessions once per sample only while blocking is past its warning threshold (see *Blocking right now* below).
- **Chat channels add nothing on the server.** The [Slack and Teams channels](Chat-Notifications.md) (from 0.52.0) are posted to over outbound HTTPS from this PC, using what SqlVitals already knows. They need no new SQL and no new permissions.
- **No agent, no service, no inbound ports.** It's a desktop app making ordinary client connections. From 0.35.0 closing its window hides it to the [notification area](Notifications-and-Tray.md) by default, so any monitoring carries on until the user stops it or chooses **Exit**. The load is the same as with the window open.

## What runs, and when

| Workload | Trigger | Frequency | Scope |
|---|---|---|---|
| **Live Metrics query** | Every **monitored** connection: those started with ▶ / Start, plus, with *Start monitoring automatically* on, the active one and those with *Monitor in background* | Every **10 s** by default (user-adjustable) | Performance counters, `sys.dm_os_ring_buffers` (`sys.dm_db_resource_stats` on Azure SQL Database), `sys.dm_exec_requests`; from 0.59.0 a count of `sys.dm_os_waiting_tasks` joined to `sys.dm_exec_sessions` (user tasks waiting now) |
| **History detail snapshot** | Same connections | Every **5 min** by default (1 / 5 / 15 / 30) | `sys.dm_os_wait_stats`, `sys.dm_io_virtual_file_stats`, `sys.dm_exec_query_stats` (top 20 by CPU), memory and performance counters |
| **Screens** (Index Health, Query Store, Plan Cache, …) | A user opens the screen or presses Refresh | On demand | **Active connection only** |
| **Query Regressions** | On demand | On demand | Query Store runtime stats, up to ~8 days; timeout raised to 2 min |
| **Query Store** tabs | A user opens the page or a tab, presses Refresh or picks a period | On demand; each tab only when opened | `sys.query_store_*` and `sys.database_query_store_options` in the connection's database: the period's runtime stats (up to 30 days, or what Query Store keeps), grouped and ranked on the server so only the top rows come back; timeout raised to 2 min; **active connection only** |
| **Processes** (blocking chains) | The Processes page is open with auto-refresh on, or the *Process Map* chart is shown on Live Metrics **while the connection is monitored** | The page's interval (5 s to 2 min); with each Live Metrics sample for the *Process Map* | `sys.dm_exec_sessions`, `sys.dm_exec_requests`, `sys.dm_exec_connections`, `sys.dm_exec_sql_text`; **active connection only** |
| **Agent Jobs** | A user opens the page or presses Refresh; every 30 s while *Every 30 s* is ticked and the page is open | On demand | `msdb` job tables (`sysjobs`, `sysjobhistory`, `sysjobactivity`, `sysjobschedules`, …) and `sys.dm_server_services`; one job's history when it's selected; **active connection only** |
| **Backups** | A user opens the page, presses Refresh or Apply | On demand, no timer | One query: `msdb.dbo.backupset` read once (newest full, differential and log per database), `backupmediafamily`, `sys.databases`, `sys.database_recovery_status`; up to 500 `backupset` rows when a database is selected; **active connection only** |
| **File I/O** | The File I/O page is open (not paused), or a user presses Refresh | The page's interval: 5 s to 1 min, 10 s by default | Two light queries per reading: `sys.dm_os_sys_info` (start time) and `sys.dm_io_virtual_file_stats` with `sys.master_files`; `sys.dm_os_volume_stats` once per file; **active connection only**, stops when the page is left |
| **Config Checks** | A user opens the page, presses Check again or Refresh | On demand, no timer | A handful of light catalog queries: `sys.configurations`, `sys.databases`, `tempdb.sys.database_files`, and `sys.dm_os_schedulers`, `sys.dm_os_nodes`, `sys.dm_os_sys_info` for the processors and memory; **active connection only** |
| **Security Audit** | A user opens the page, presses Audit again or Refresh | On demand, no timer | Light catalog queries: `sys.server_principals`, `sys.server_role_members`, `sys.server_permissions`, `sys.sql_logins`, `sys.databases`, the connection's own `sys.login_token`, and `sys.database_principals` once per database for orphaned users; **active connection only** |
| **DB Storage** | A user opens the page or presses Refresh | On demand, no timer | `sys.master_files`, `sys.databases`, `sys.configurations`; the used space of each database the login can open, read with one light `sys.database_files` query in that database (through its `sys.sp_executesql`) into a temporary table that is dropped at the end of the batch; **active connection only** |
| **Deadlocks** | A user opens the page or presses Refresh | On demand, no timer | Reads the existing **system_health** session: its `.xel` files through `sys.fn_xe_file_target_read_file`, filtered to `xml_deadlock_report` on the server, or its ring buffer. Creates nothing; timeout raised to 2 min |
| **SP Trace** | A user presses Start | Poll interval chosen by the user | Extended Events session, or `sys.dm_exec_procedure_stats` |
| **Blocking right now** (a server's Overview) | The page is open, the connection is monitored, and its Blocking check is past its warning threshold | Once per sample, while all three hold | The Processes query (`sys.dm_exec_sessions`, `sys.dm_exec_requests`, `sys.dm_exec_connections`, `sys.dm_exec_sql_text`), through that connection's monitoring, so it may be a connection other than the active one |
| **Since you last looked** (a server's Overview, from 0.59.0) | A user opens the Overview or presses Refresh, while the connection is monitored and answering | Once per opening or Refresh | The Deadlocks query (system_health) and the Agent Jobs query (msdb), through that connection's monitoring; regressed queries come from the local history, not the server |

Heavy screens **never run in the background**, and never against connections other than the active one. The light exceptions are the Overview's *Blocking right now* and *Since you last looked*, above, which only run while a user is looking at a monitored server. *Since you last looked* reads the same system_health files as Deadlocks (see [Heaviest queries](#heaviest-queries)), once each time the Overview opens.

### What runs by default

| | Start monitoring automatically **off** (new installations from 0.46.0) | **On** (upgraded installations, or turned on) |
|---|---|---|
| **When SqlVitals starts or connects** | Nothing, apart from the Azure SQL Database edition check (`SERVERPROPERTY('EngineEdition')`) on connecting | Live Metrics query and history snapshots for the active connection and each with *Monitor in background* |
| **Opening a server's screen, or switching server** | Nothing new | It becomes the active connection, which is monitored too while Start monitoring automatically is on |
| **A server's Overview open** | Nothing: it says the server isn't monitored, until **Start monitoring** | *Since you last looked* (deadlocks and jobs) once, and *Blocking right now* while blocking is past its threshold |
| **Live charts page open** | Nothing: it shows *Monitoring is off* until **▶ Start monitoring**; no Process Map polling | The live metrics query, and the Process Map while that chart is shown |
| **Other pages** | Their own queries, while open, as in the table above | The same |

A user can start and stop monitoring for any connection at any time; every start and stop, with who did it, is written to the [activity log](Settings.md#activity-log). With the setting off SqlVitals puts less load on a server than before 0.46.0: no new queries were added, and none run until asked for.

### Heaviest queries

The most expensive reads, worth knowing on very large servers:

| Screen | Why it can be heavy | Mitigation |
|---|---|---|
| Index Health → Fragmentation | `sys.dm_db_index_physical_stats` scans index pages | Uses the *Min Page Count* filter; runs only on demand |
| Implicit Conversions, Plan Cache Health | Searches cached plan XML | Runs only on demand |
| Query Regressions (Query Store source) | Reads a week of runtime stats | Runs only on demand; per database |
| Query Store tabs over 7 or 30 days | Reads every runtime-stats row in the period (one per plan, interval and execution type) | Runs only on demand, one tab at a time; grouped on the server; per database |
| Deadlocks | Reads every system_health `.xel` file (tens of MB or more, depending on version and settings) | Runs only on demand; the server returns only the deadlock events; the XML is parsed on your PC |

### Protective behaviour

- **`READ UNCOMMITTED`** is prepended to every query (`QuerySettings:UseNoLock`), so monitoring never waits on locks it's reporting on.
- **Command timeout** (default 30 s, configurable) cancels any runaway query.
- **Backoff:** unreachable servers are retried with exponential backoff, up to every 5 minutes.
- **One connection per query:** connections are opened per call and returned to the ADO.NET pool; no long-lived sessions are held.
- **SP Trace limits:** filtered to the connected database and to calls ≥ *Min duration* **on the server side**; ring buffer capped at 4 MB / 1,000 events; dropped-event warnings are surfaced.
- **MFA and password prompts** are never triggered by monitoring: a connection without a saved password, or an Entra MFA connection not signed in to this session, can't be started until the user connects to it.

## Permissions

| Feature | SQL Server / Managed Instance | Azure SQL Database |
|---|---|---|
| Every screen except SP Trace per-call mode | `VIEW SERVER STATE` | `VIEW DATABASE STATE` |
| SP Trace, DMV fallback mode | `VIEW SERVER STATE` | `VIEW DATABASE STATE` |
| SP Trace, Extended Events mode | `VIEW SERVER STATE` **+** `ALTER ANY EVENT SESSION` | `VIEW DATABASE STATE` **+** `ALTER ANY DATABASE EVENT SESSION` |
| Agent Jobs | `SELECT` on msdb's job tables (sysadmin has it) | ➖ no SQL Server Agent |
| Backups | `SELECT` on `msdb.dbo.backupset` and `backupmediafamily` (sysadmin has it) | ➖ Azure takes the backups; none are in msdb |
| Config Checks | `VIEW SERVER STATE` for the processors and memory; without it those checks are partly *Not checked* | Nothing extra |
| Security Audit | `VIEW ANY DEFINITION` to list the sysadmin members and orphaned users; without it those two show *Not checked* | ➖ not available (no sysadmin, sa or TRUSTWORTHY; the button is marked N/A) |

```sql
-- Minimum: every screen (SP Trace in fallback mode)
GRANT VIEW SERVER STATE TO [sqlvitals_login];      -- SQL Server / MI
GRANT VIEW DATABASE STATE TO [sqlvitals_user];     -- Azure SQL Database, in the user DB

-- Optional: per-call SP Trace
GRANT ALTER ANY EVENT SESSION TO [sqlvitals_login];            -- SQL Server / MI, in master
GRANT ALTER ANY DATABASE EVENT SESSION TO [sqlvitals_user];    -- Azure SQL Database
```

> ⚠️ `ALTER ANY EVENT SESSION` lets the login create, alter, start, stop and drop **any** event session on the server, not only SqlVitals'. Withhold it if that's too broad. SP Trace then falls back to per-procedure aggregates automatically.

On SQL Server 2022 and later, `VIEW SERVER STATE` includes the finer-grained `VIEW SERVER PERFORMANCE STATE`, which is what the [Deadlocks](Deadlocks.md) page needs to read system_health; granting that one alone is enough for it.

### SQL Agent jobs

The [Agent Jobs](Agent-Jobs.md) page reads SQL Server Agent's tables in `msdb`. Members of **sysadmin** can already; for another login, give it a user in msdb and read access to those tables:

```sql
USE msdb;
CREATE USER [sqlvitals_login] FOR LOGIN [sqlvitals_login];   -- skip if it already has one
GRANT SELECT ON dbo.sysjobs         TO [sqlvitals_login];
GRANT SELECT ON dbo.sysjobhistory   TO [sqlvitals_login];
GRANT SELECT ON dbo.sysjobactivity  TO [sqlvitals_login];
GRANT SELECT ON dbo.syssessions     TO [sqlvitals_login];
GRANT SELECT ON dbo.sysjobschedules TO [sqlvitals_login];
GRANT SELECT ON dbo.sysschedules    TO [sqlvitals_login];
GRANT SELECT ON dbo.syscategories   TO [sqlvitals_login];
```

This is read-only: the login can't start, stop or change a job. With `VIEW SERVER STATE` as well, the page can also tell whether the Agent service is running (`sys.dm_server_services`).

### Backups

The [Backups](Backups.md) page reads the backup history in `msdb`. Members of **sysadmin** can already; for another login, give it a user in msdb and read access to the two tables:

```sql
USE msdb;
CREATE USER [sqlvitals_login] FOR LOGIN [sqlvitals_login];   -- skip if it already has one
GRANT SELECT ON dbo.backupset         TO [sqlvitals_login];
GRANT SELECT ON dbo.backupmediafamily TO [sqlvitals_login];
```

It also reads `sys.databases` and `sys.database_recovery_status`, which every login can see for the databases it can see. This is read-only: the login can't take, restore or delete a backup.

### Security Audit

The [Security Audit](Security-Audit.md) reads logins, role members and each database's users. A login without `VIEW ANY DEFINITION` sees only itself and a few system principals, so the page lists the sysadmin members and orphaned users only when it has it (sysadmin does):

```sql
GRANT VIEW ANY DEFINITION TO [sqlvitals_login];   -- SQL Server / MI
```

It shows the definitions of logins, users, roles and objects, but no data, and changes nothing. The sa login and TRUSTWORTHY are checked without it. Orphaned users are looked for only in databases the login can open.

Some screens read database-scoped views (Index Health, Stale Stats, Query Store, DB Storage → Top Tables) for the **connection's database**. The login needs to be able to connect to that database. The Query Store page's hints grid (SQL Server 2022+) also needs `VIEW SERVER STATE` (`VIEW SERVER PERFORMANCE STATE` on 2022), which the other screens already need; without it the grid says so and the rest of the tab still loads.

**DB Storage → Database Files** shows each file's used and free space only for databases the login can open (`HAS_DBACCESS`); for the rest they're blank. Sizes, growth and settings are shown for every database either way.

**Check what a login has:**

```sql
-- SQL Server / MI
SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE')       AS CanReadDmvs,
       HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY EVENT SESSION') AS CanTrace;

-- Azure SQL Database
SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE')              AS CanReadDmvs,
       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER ANY DATABASE EVENT SESSION') AS CanTrace;
```

## Compatibility

| | SQL Server (on-premises / VM) | Azure SQL Managed Instance | Azure SQL Database |
|---|---|---|---|
| All monitoring screens | ✅ | ✅ | ✅ (server-level parts skipped) |
| DB Storage: TempDB files, `sp_configure` | ✅ | ✅ | ➖ not available, skipped |
| TempDB page (`sys.master_files`) | ✅ | ✅ | ➖ not available (the button is marked N/A) |
| Query Store / Query Regressions (Query Store source) | 2016+ | ✅ | ✅ |
| Query Store: Waits tab, Log used / TempDB used, *Forced by* | 2017+ | ✅ | ✅ |
| Query Store: Query Store hints | 2022+ | ✅ | ✅ |
| SP Trace | Server-scoped XE session | Server-scoped | Database-scoped (`ON DATABASE`) |
| Deadlocks (system_health) | ✅ | ✅ | ➖ not available (no system_health) |
| Agent Jobs (SQL Server Agent) | ✅ (not Express, which has no Agent) | ✅ | ➖ not available (no Agent; the button is marked N/A) |
| Backups (msdb backup history) | ✅ | ✅ | ➖ not available (Azure backs up by itself; the button is marked N/A) |
| File I/O (`sys.dm_io_virtual_file_stats`) | ✅ Every database | ✅ Every database | ⚠️ The current database's files only; no volumes |
| Config Checks | ✅ | ✅ (compatibility level held to the highest level in use) | ⚠️ This database's MAXDOP, auto-shrink, page verify and compatibility level only |
| Security Audit | ✅ | ✅ (Microsoft Entra logins and groups too) | ➖ not available (the button is marked N/A) |
| Index `ONLINE = ON` in scripts | Enterprise / Developer | ✅ | ✅ |

The edition is detected at runtime with `SERVERPROPERTY('EngineEdition')` (5 = Azure SQL Database, 8 = Managed Instance), and queries switch automatically.

On Azure SQL Database the **TempDB**, **Agent Jobs**, **Backups** and **Security Audit** buttons are disabled: from 0.45.1 they're dimmed and marked **N/A**, and hovering one says why. They come back when you switch to a server that has them.

## Identifying SqlVitals on your server

SqlVitals sessions use the login you configured, and from 0.44.1 name themselves **SqlVitals** (`program_name` in `sys.dm_exec_sessions`, *ApplicationName* in traces and audits), unless *Additional parameters* sets another `Application Name`. You can filter on it, or cap it with a Resource Governor classifier. The SP Trace event session is named `SqlVitals_SpTrace` (configurable with `TraceSettings:SessionName`).
