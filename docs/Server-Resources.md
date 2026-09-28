# Server Resources

Five screens show how the server's memory, TempDB, storage, configuration and connections are used.

| Screen | Answers |
|---|---|
| [🗄️ TempDB](#tempdb) | How full is TempDB, and who is filling it? |
| [🧠 Memory Grants](#memory-grants) | Where is memory going, and are queries waiting for it? |
| [🗄️ DB Storage](#db-storage) | How big are the databases and files, and how is the server configured? |
| [🔌 App Connections](#app-connections) | Which applications are connected, and what load do they create? |
| [📈 Perfmon](#perfmon) | Any SQL Server performance counter, charted |

---

## TempDB

> **Where:** sidebar → 🗄️ TempDB · **Use it when:** the health dot flags TempDB space, or you see `PAGELATCH` waits

- **Cards:** the version store's size (red once it holds anything), free space in TempDB, transactions open in TempDB, active temp tables, and temp tables waiting to be dropped. How fast versions are generated and cleaned up is charted on [Perfmon](#perfmon), which shows per-second counters as rates.
- **TempDB File Space: Used vs Free (MB)**: a chart per TempDB file, at its size now.
- **Top Sessions by TempDB Allocation**: who is using TempDB now. **Net KB** is the total. **User Obj KB** covers temp tables and table variables. **Internal KB** covers sorts, hashes and spools, usually from big queries. Also shown: wait type, wait ms and status.

| Severity | Net allocation |
|---|---|
| Info | ≤ 100 MB |
| Warning | > 100 MB |
| Critical | > 1 GB |

A session with large **Internal KB** is usually one query sorting or hashing a lot of data. Look at its plan for a missing index or a bad estimate.

---

## Memory Grants

> **Where:** sidebar → 🧠 Memory Grants · **Use it when:** you see `RESOURCE_SEMAPHORE` waits, or *memory grants pending* on the health dot

**Cards** at the top: Physical Memory, SQL Server Memory, Target Memory, Buffer Pool, Plan Cache, Available Physical, Page File, **System Memory State** and Memory Model.

**Memory Model** is how SQL Server allocates memory: `CONVENTIONAL`, `LOCK_PAGES` (the service account has *Lock pages in memory*) or `LARGE_PAGES`. It shows **N/A** on Azure SQL Database and on builds that don't report it, such as SQL Server 2016 RTM and 2012 before SP4.

Three views:

| View | Shows |
|---|---|
| **Overview** | A memory trend chart. Pick an **Interval** (10s, 30s, 60s) and press **▶ Start**. |
| **Memory Clerks** | The top consumers of SQL Server memory (buffer pool, plan cache, …) in MB and % of total |
| **Memory Grants** | Queries holding or **waiting for** workspace memory: requested, granted and used KB, wait ms, **Pending** |

| Severity | Rule |
|---|---|
| **Critical** | The query is **waiting** for its grant (pending) |
| **Warning** | Granted more than 1 GB |
| Info | Otherwise |

**Reading it:** a grant that's much bigger than what's **Used** means the optimizer over-estimated. That's often a stale-statistics or parameter-sniffing problem, and it starves other queries of memory.

---

## DB Storage

> **Where:** sidebar → 🗄️ DB Storage · **Use it when:** checking disk space, growth settings or basic configuration

**Cards:** Total DB Size, Data Files, Log Files, TempDB Size, **Max DOP** and **Cost Threshold** for parallelism.

| Tab | Shows | Look for |
|---|---|---|
| **📂 Database Files** | Every data and log file of every online database: state, recovery model, compatibility level, size, used, free, max size, auto-growth, auto-shrink, Query Store on, encrypted, path. **Used** and **Free** are blank for a database your login can't open | Files close to **Max MB**; **percent** auto-growth on big files; **Auto-Shrink on** (almost always bad) |
| **🌡️ TempDB Files** | TempDB data and log files | Data files of unequal size |
| **⚙️ Server Config** | Key `sp_configure` settings, with min, max, whether dynamic, and a description | *max server memory* left at the default; *cost threshold for parallelism* at 5 |
| **📋 Top Tables** | The 20 largest tables in the current database: rows, data, index and unused MB | Tables with more index than data |

On **Azure SQL Database**, server-level parts (TempDB files, `sp_configure`) aren't available and are skipped automatically.

> **Tip:** [Configuration Checks](Configuration-Checks.md) checks these settings for you: MAXDOP, cost threshold, max server memory, TempDB's files, auto-shrink, page verify and compatibility level, with the value recommended for this server.

---

## App Connections

> **Where:** sidebar → 🔌 App Connections · **Use it when:** you want to know which application causes the load, or you suspect a connection leak

Connections grouped **by application** (program name): number of connections, active requests, CPU time, memory, logical reads, reads, writes, first login and last request.

**Reading it:** hundreds of connections from one application with few active requests usually means a connection pool that's too big, or connections that aren't closed.

---

## Perfmon

> **Where:** sidebar → 📈 Perfmon · **Use it when:** you need a specific SQL Server counter that the other screens don't show

1. Tick counters in **Select Counters** on the left (**Select All** / **Clear All**). Up to **12** are charted at once.
2. Set **Auto-refresh** and press **Start**, or use the sidebar **Refresh** for one reading.
3. Use the **range picker** (1h, 24h, 7d, Custom…) to chart recorded history, and **Compare to baseline** for last week. See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md).

Counters include batch requests, compilations, lock waits and timeouts, log flushes, memory grants, page life expectancy and more. Per-second counters are shown as rates.

---

### Under the hood

| Screen | Source |
|---|---|
| TempDB | `sys.dm_db_session_space_usage` joined to `sys.dm_exec_sessions` / `_requests`; file sizes and space from `tempdb.sys.database_files`, and point-in-time TempDB performance counters (`TempDbRepository`) |
| Memory Grants | `sys.dm_os_sys_info`, `sys.dm_os_sys_memory`, `sys.dm_os_process_memory`, `sys.dm_os_memory_clerks`, `sys.dm_exec_query_memory_grants`, memory manager counters |
| DB Storage | `sys.master_files`, `sys.databases`, each database's `sys.database_files`, `sys.configurations`, `sys.partitions` + `sys.allocation_units` (top tables) |
| App Connections | `sys.dm_exec_sessions` / `sys.dm_exec_requests` grouped by `program_name` |
| Perfmon | `sys.dm_os_performance_counters` |

- **Azure SQL Database** (`SERVERPROPERTY('EngineEdition') = 5`) blocks `sys.master_files`, cross-database `tempdb.sys.database_files` and `sys.configurations`. `GetDatabaseStorageAsync` detects the edition and switches to database-scoped queries (`sys.database_files`, `sys.database_scoped_configurations`), skipping TempDB files.
- **Memory model** is `sys.dm_os_sys_info.sql_memory_model_desc`, which arrived in a service pack (2012 SP4 and 2016 SP1, per Microsoft's docs), so the major version can't tell whether it exists. `GetMemorySnapshotAsync` first asks `sys.all_columns` whether the column is there and names it only if it is; naming a missing column fails the whole query with error 207. Before 0.43.1 it asked for `memory_model_desc`, a column no version has, and the page failed on every SQL Server 2016 or later.
- **Perfmon history** comes from the `counter_values` table, recorded from version 0.30. Per-second counters are stored as rates over the snapshot interval; zeros aren't stored.
- **Used space** comes from `FILEPROPERTY(name, 'SpaceUsed')`, which only sees the **current** database's files. So TempDB's files are read through `tempdb.sys.sp_executesql`, and DB Storage reads each database it can open (`HAS_DBACCESS`) through that database's `sys.sp_executesql`, collected in a temporary table in the same batch. `sys.master_files` holds TempDB's **startup** size, so TempDB's size is read from `tempdb.sys.database_files` too. Before 0.44.1 every file outside the connection's database showed 0 MB used, and TempDB showed its startup size (#109).
- **TempDB counters** are the point-in-time ones (`cntr_type` 65792). The version store counters have no instance, and *Active Transactions* is under the *Databases* object for instance `tempdb`; before 0.44.1 the query asked for the version store counters with instance `tempdb`, so their cards never appeared. The rate counters are totals since the server started, which only mean something as the difference between two readings, so they're left to Perfmon.
- **Top Tables** is one row per table: sizes add up every partition of every index, and **Rows** is the heap's or clustered index's, summed over its partitions. Before 0.44.1 a partitioned table, or one with a filtered index, was split over several rows.
- **App Connections** sums CPU time and memory as `bigint`: `SUM` of an `int` column stays `int`, and on a busy server it overflowed (error 8115).
- **Older versions:** `sys.databases.is_query_store_on` (SQL Server 2016+) is only named where `sys.all_columns` says it exists; before that Query Store is shown as off.
