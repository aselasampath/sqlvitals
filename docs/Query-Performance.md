# Query Performance

Four screens find the queries that cost the most and the plan problems behind them.

| Screen | Answers |
|---|---|
| [🔥 Resource Queries](#resource-queries) | Which queries use the most CPU and I/O? |
| [📋 Query Store](#query-store) | What were the top CPU queries in the last 24 hours? |
| [⚠️ Implicit Conv.](#implicit-conversions) | Which queries lose index seeks to data-type mismatches? |
| [🔎 Plan Cache Health](#plan-cache-health) | Are plans being reused, and are any badly estimated? |

To find queries that **got slower**, see [Query Regressions](Query-Regressions.md). For **stored-procedure calls as they happen**, see [SP Trace](SP-Trace.md).

---

## Resource Queries

> **Where:** sidebar → 🔥 Resource Queries · **Use it when:** CPU is high, or I/O waits dominate

The most expensive queries currently in the **plan cache**, on three tabs:

| Tab | Sorted by | Best for |
|---|---|---|
| **By Logical Reads** | Total logical reads | Queries that read lots of data: usually missing indexes or scans |
| **By CPU** | Total CPU time | High-CPU servers |
| **Highest Logical Reads** | Total logical reads, with each query's **Last Reads** | Checking whether the latest run is still as heavy |

### How to use it

1. Pick a **Row Limit** (how many queries to load) at the top.
2. Choose a tab. Click a column header to sort, for example by **Avg CPU µs** to find queries that are expensive *per run*.
3. **View Plan** opens the query's execution plan. See [Execution plans](#execution-plans).
4. Type in **Filter rows…** to find a table, procedure or piece of text. See [Working with Grids](Working-with-Grids.md).
5. **⎘ Copy All** copies every row shown, with headers, in the current order.

### Reading it

- **Total** columns find what costs the server most overall. **Avg** columns find what's slow per run. A cheap query that runs a million times can cost more than a slow one that runs once.
- CPU and elapsed times are in **microseconds** (µs). 1,000,000 µs = 1 second.
- The plan cache is cleared on restart and under memory pressure. A query that ran yesterday may no longer be listed. Use [Query Store](#query-store) or [history](History-Time-Ranges-and-Baselines.md) for older activity.

---

## Query Store

> **Where:** sidebar → 📋 Query Store · **Needs:** Query Store turned on for the database (SQL Server 2016+, Azure SQL)

The **top 20 queries by CPU in the last 24 hours**, from the connected database's Query Store. Unlike the plan cache, Query Store survives restarts.

| Column | Meaning |
|---|---|
| **Query ID** | Query Store's ID. Use it in SSMS's Query Store reports. |
| **Executions**, **Total / Avg CPU ms**, **Total Reads**, **Avg Duration ms** | Cost in the last 24 hours |
| **Forced Plan** | Whether a plan has been forced for this query |
| **Last Executed** | When it last ran |
| **Status** | Severity by total CPU in 24 h: **Critical** > 60 s · **Warning** > 10 s · **Info** below |
| **Query Text** | The first line of the statement, so you can find a query with the filter box |

If Query Store is off, the status card at the top reads **Not Enabled**. Turn it on with `ALTER DATABASE [YourDb] SET QUERY_STORE = ON;`, or talk to your DBA. If it has gone **read-only**, the page says why, for example *Query Store reached MAX_STORAGE_SIZE_MB*.

---

## Implicit Conversions

> **Where:** sidebar → ⚠️ Implicit Conv.

Cached plans that contain `CONVERT_IMPLICIT`, sorted by execution count.

**Why it matters:** when a query compares values of different data types, for example an `NVARCHAR` parameter against a `VARCHAR` column, SQL Server converts one side. If it has to convert the **column**, it often can't seek on that column's index and scans instead.

**Typical fix:** make the application's parameter type match the column, e.g. send `VARCHAR` for a `VARCHAR` column. In .NET, set the parameter's `SqlDbType` explicitly instead of passing a string.

---

## Plan Cache Health

> **Where:** sidebar → 🔎 Plan Cache Health

Five tabs, each looking for a different plan problem:

| Tab | Finds | Usual fix |
|---|---|---|
| **Multi-Plan Queries** | The same query with many cached plans (**parameterisation failures**): usually literals in the SQL instead of parameters | Parameterise the query; consider *optimize for ad hoc workloads* or forced parameterisation |
| **Key / RID Lookups** | Plans that seek an index and then look up the rest of the row, in high-execution queries | Add the looked-up columns to the index as `INCLUDE` columns |
| **Cardinality Misestimates (live)** | Queries running **now** whose actual row counts are ≥ 1,000× off the estimate | Update statistics ([Stale Stats](Index-and-Statistics-Maintenance.md#stale-statistics)), simplify predicates |
| **Skewed Parallelism (live)** | Parallel queries running **now** whose threads have very uneven work (≥ 5×) | Often caused by skewed data or bad estimates; review the plan |
| **Plan Guides** | Plan guides: plans frozen by hand | Check they're still wanted, and not disabled or stale |

*(live)* tabs show only queries running at that moment. Refresh while the problem is happening.

---

## Execution plans

**View Plan** (Resource Queries, SP Trace) and the **Now / Before** buttons (Query Regressions) open the **Query Execution Plan** window. It draws the plan **graphically, like SSMS**:

- One diagram per statement, with its cost relative to the batch and any **missing-index** suggestion.
- Each operator's icon, object and **cost %**. Operators at 25 % or more are highlighted, arrows get thicker with the row count, and badges flag warnings (spills, missing statistics, implicit conversions…) and parallelism.
- In actual plans, elapsed time and actual vs. estimated rows, with estimates **10× off** in red.
- Hover for details, click an operator for **every property**, **Find**, zoom, pan, and **Export diagram as PNG**.
- The **XML** tab has the raw plan: **Copy XML**, or **Save as .sqlplan** for SSMS or Azure Data Studio.
- *"No execution plan available"* means SQL Server has already evicted the plan.

See [Execution Plans](Execution-Plans.md) for how to read the diagram.

---

### Under the hood

| Screen | Source |
|---|---|
| Resource Queries | `sys.dm_exec_query_stats` + `sys.dm_exec_sql_text` + `sys.dm_exec_query_plan` |
| Query Store | `sys.query_store_query`, `_plan`, `_runtime_stats`, `_query_text` for the connected database |
| Implicit Conversions | Cached plan XML (`sys.dm_exec_cached_plans` + `sys.dm_exec_query_plan`) searched for `CONVERT_IMPLICIT` |
| Plan Cache Health | `sys.dm_exec_cached_plans` / `sys.dm_exec_query_stats` grouped by `query_hash`; live tabs use `sys.dm_exec_query_profiles`; plan guides from `sys.plan_guides` |

- These run **on demand** against the active connection only, never in the background. Plan-XML searches are the heaviest queries SqlVitals runs; on a server with a very large plan cache they may need a longer **Command timeout** (Settings).
- The live tabs depend on lightweight query profiling, which is on by default from SQL Server 2019 and in Azure SQL. On older versions they may be empty.
- The live tabs read `estimate_row_count` and `row_count` from `sys.dm_exec_query_profiles`. A parallel node has one row per thread: Cardinality Misestimates sums them into one row per node, and Skewed Parallelism compares the worker threads (thread 0, the coordinator, is left out). Before 0.43.2 both asked for `estimated_row_count` and `actual_row_count`, columns no version has, so the whole page failed with error 207.
- Only the summary and Multi-Plan Queries are required. If Key / RID Lookups, a live tab or Plan Guides hits a missing column or object (error 207 or 208), that tab is left empty and the rest of the page still loads. Query Store does the same when its catalog views don't exist (SQL Server 2014 and earlier).
- **Query Store** (fixed in 0.44.1, #109): `readonly_reason` is a bit map (an `int`), turned into words; reading it as text failed the page. The plan type (`sys.query_store_plan.plan_type_desc`) is SQL Server 2022+, so it's named only where `sys.all_columns` says it exists; before that every plan is a compiled plan. `last_execution_time` is a `datetimeoffset` in UTC. Before 0.44.1 the top queries never loaded, on any version.
- **Multi-Plan Queries** shows the query hash as hex (`CONVERT(varchar(18), query_hash, 1)`, e.g. `0x1A2B…`), so it can be matched with the hash elsewhere. Before 0.44.1 it cast the 8 bytes to characters.
- SQL lives in `WaitStatsRepository.cs` and `PlanCacheHealthRepository.cs` as raw string literals. See [Architecture](Architecture.md#where-the-sql-lives).
