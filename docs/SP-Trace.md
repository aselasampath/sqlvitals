# SP Trace

**🛰️ SP Trace** shows **stored-procedure calls as they happen**: which procedure, how long it took, how much CPU and I/O it used, and who called it. It's like a lightweight, stored-procedure-only Profiler.

> **Where:** sidebar → 🛰️ SP Trace · **Use it when:** you need to see exactly what an application is calling, or catch an intermittently slow procedure · **Needs:** `VIEW SERVER STATE`; per-call rows also need an Extended Events permission (see [Permissions](#permissions))

## How to use it

1. Set **Min duration (ms)**: only calls at least this slow are captured. **On a busy server, start high** (e.g. 100 ms) and lower it if needed.
2. Pick a **Poll** interval: how often new calls are fetched.
3. Press **● Start Trace**. The badge in the status strip shows the [capture mode](#two-capture-modes).
4. Watch the **Live Calls** tab fill. **Auto-scroll** keeps the newest call in view. **Clear** empties the list.
5. Press **■ Stop Trace** when you're done. The trace also stops when you leave the page or exit the app. Closing or minimizing the window only [hides it to the notification area](Notifications-and-Tray.md#running-in-the-notification-area) by default, and a running trace **keeps running** until you stop it or choose **Exit**.

### Live Calls tab

One row per call: **Time, Procedure, Dur (ms), CPU (ms), Reads, Writes, Rows, SPID, Login, Application, Host, Event**.

Narrow the list with the filters above it:

| Filter | Shows |
|---|---|
| **Search** | Procedure name or statement text contains… |
| **Database / Login / Application / Host / Event** | Only that value |
| **SPID** | One session |
| **Dur ≥ / CPU ≥ / Reads ≥** | Only calls at least this heavy |
| **Hide sp_reset_connection** | Hides connection-pool resets, which are noise for most investigations |
| **Hide sp_executesql** | Hides parameterised ad-hoc batches. Their statement text is often the real query, so untick this if you're hunting an ORM's SQL. |

**Clear filters** resets them. Select a row to see its **Statement**. **View Plan** shows the procedure's cached execution plan as a [graphical plan](Execution-Plans.md), and **Copy** copies the statement.

### By Procedure tab

One row per procedure: **Executions, Avg (ms), Total (ms), Max ever (ms), Avg CPU (ms), Avg Reads, Last Run**. Sort by **Total (ms)** to see what costs most overall, or by **Avg (ms)** to see what's slow per call.

## Two capture modes

SqlVitals picks the mode automatically when you press Start. The badge shows which is active, and the fallback mode always says why.

| Mode | You get | Needs |
|---|---|---|
| **Extended Events** | **Every call** as its own row, with login, application, host, SPID and statement | `VIEW SERVER STATE` + permission to create an event session |
| **DMV fallback** | **Per-procedure totals** for each poll window: executions, duration, CPU, reads | `VIEW SERVER STATE` only |

A missing permission never breaks the page. It simply falls back.

## Permissions

To get per-call rows, a DBA grants:

```sql
-- SQL Server (on-premises) / Managed Instance — server-level, in master
GRANT ALTER ANY EVENT SESSION TO [your_login];

-- Azure SQL Database — database-level, in the user database
GRANT ALTER ANY DATABASE EVENT SESSION TO [your_user];
```

> ⚠️ **What this allows:** the login can create, start, stop and drop **any** Extended Events session on that server (or database), not just SqlVitals'. If that's too broad for your environment, don't grant it. The DMV fallback still reports per-procedure executions, duration, CPU and reads.

## Impact on the server

Tracing a busy server isn't free, so SP Trace:

- **never starts on its own**; you press Start;
- captures **only the connected database**;
- caps its buffer (4 MB, 1,000 events) and applies **Min duration** on the server, so fast calls cost almost nothing;
- **warns you** in the status strip if events are dropped or truncated, instead of making the server look quiet.

---

### Under the hood

**Event session lifecycle.** The session is named `SqlVitals_SpTrace` (`TraceSettings:SessionName`), uses a `ring_buffer` target, and is **created on Start and dropped on Stop**. It's also dropped when you navigate away and when the app exits (hiding the window to the notification area doesn't count). `MainWindow.OnClosing`, on a real exit, calls `SpTracePage.PrepareForShutdown()` to stop the poll timer, then waits up to 20 s for the drop. Any session left by a crash is dropped by name before the next one is created. Nothing is left running on the server.

**Two events are captured**, because how the caller is written decides which one fires:

| Caller | Event |
|---|---|
| `CommandType.StoredProcedure` (RPC) | `rpc_completed` |
| `EXEC dbo.usp_X` sent as a batch | `module_end` |

`module_end` is narrowed to stored procedures with `object_type = 8272`. **Azure SQL Database rejects that predicate**, so `CreateAndStartSessionAsync` retries without it and filters functions and triggers out client-side (`SpTraceEvent.IsStoredProcedure`).

**Scope.** Azure SQL Database uses a database-scoped session (`ON DATABASE`, `sys.dm_xe_database_sessions` / `_session_targets`). On-premises uses the server-scoped equivalents. The branch follows `IsAzureSqlDatabaseAsync()`.

**Mode choice.** `SpTraceRepository.CanCreateEventSessionAsync` runs `HAS_PERMS_BY_NAME(... 'ALTER ANY EVENT SESSION')` (or the database equivalent) at start.

**DMV fallback** diffs two snapshots of `sys.dm_exec_procedure_stats` (`ProcedureStatsDelta`) to get per-window activity.

**Parsing.** Ring-buffer XML is shredded in C# (`SpTraceXmlParser`), not with T-SQL `.nodes()`, so the monitored server doesn't spend CPU on it. Both helpers are pure and carry most of the unit tests.

**Removing a leftover session by hand** (should never be needed):

```sql
-- On-premises
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = 'SqlVitals_SpTrace')
    DROP EVENT SESSION [SqlVitals_SpTrace] ON SERVER;

-- Azure SQL Database
IF EXISTS (SELECT 1 FROM sys.database_event_sessions WHERE name = 'SqlVitals_SpTrace')
    DROP EVENT SESSION [SqlVitals_SpTrace] ON DATABASE;
```
