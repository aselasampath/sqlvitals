# Query Regressions

A **query regression** is a query that suddenly runs slower than it used to, often after a deployment, a statistics update or a server restart gave it a worse execution plan. The **🐢 Query Regressions** screen finds these for you, before users complain.

> **Where:** sidebar → 🐢 Query Regressions · **Use it when:** "it was fine last week", after a release, or as a daily check · **Needs:** Query Store on the database, *or* a saved connection with monitoring history

## How it works, in one sentence

For each query, SqlVitals compares the **average duration and CPU per execution** in a **recent** period with a **baseline** period, and lists the queries where either went up by more than your threshold.

## How to use it

Set the options at the top. The check runs straight away when you change *Recent*, *Compared with* or *Source*.

| Option | Choices | Default | Tip |
|---|---|---|---|
| **Recent** | Last hour, last 4 hours, last 24 hours | Last 24 hours | Use *last hour* right after a deployment |
| **Compared with** | *The 7 days before* · *The same time last week* | The 7 days before | *Same time last week* is fairer when load depends on the day of the week, e.g. month-end or Monday jobs |
| **Source** | Auto · Query Store · Monitoring history | Auto | See [Where the data comes from](#where-the-data-comes-from) |
| **Flag a rise of more than** | Any percentage | 50 % | 50 = more than 1.5× slower; 100 = more than doubled |
| **with at least … executions in each period** | Any number | 5 | Stops one odd run from flagging a query. Set **1** for jobs that run once a day. |

Press **↻ Check** (or **Enter** in either box) after changing the threshold or executions. The threshold and minimum executions are remembered.

## Reading the results

The list starts with the **costliest regression** (at most 200 rows).

| Column | Meaning |
|---|---|
| **Query**, **Database** | Which query |
| **Slower in** | Whether *duration*, *CPU* or both crossed the threshold |
| **Duration before / now ms**, **Duration change** | Average duration in each period, and the change in % |
| **CPU before / now ms**, **CPU change** | The same for CPU |
| **Runs before / now** | Executions in each period |
| **Extra time ms** | How much **longer** the recent runs took in total than they would have at the old average. The best measure of real impact. |
| **Plan change** | **New plan** when the recent plan never ran in the baseline. This is the classic sign of a plan regression. |
| **Execution plan** | **Now** opens the plan used most recently. **Before** (Query Store only) opens the baseline plan when it was different. Both open as a [graphical plan](Execution-Plans.md), so you can open them side by side and see what changed. |
| **Query Text** | The statement |

Figures that crossed the threshold are shown **in red**.

### What to do with a regression

1. Open **Now** and **Before** and compare them. Look for a scan that used to be a seek, a different join type, or a much lower row estimate.
2. **New plan** + old plan was fine → in SSMS, Query Store can **force** the old plan as a quick fix while you find the cause.
3. No plan change → the query may just be processing more data, or waiting on something else. Check [Wait Statistics](Wait-Statistics.md) and [Stale Statistics](Index-and-Statistics-Maintenance.md#stale-statistics).

## Where the data comes from

| | Query Store | Monitoring history |
|---|---|---|
| **Covers** | Every query in the **connected database** | The **whole server**, but only the **top 20 queries by CPU** at each snapshot |
| **Plans** | Now and Before | Now only, and only if still in the plan cache |
| **Needs** | Query Store on (read-write or read-only), SQL Server 2016+ or Azure SQL | A saved connection monitored in the background for long enough to cover both periods |
| **Clock** | The server's | This PC's |

**Auto** uses Query Store when it's on, otherwise the monitoring history, and says which in the status line.

## Things to know

- **Noise is ignored.** A metric whose recent average is under **1 ms** is never flagged: 0.1 → 0.3 ms is +200 %, but not worth acting on. A zero baseline gives no percentage.
- Checks run **on demand** against the active connection only, never in the background. Reading a week of Query Store data can take a while on a large database, so the check may run for up to 2 minutes.
- The sidebar **↻ Refresh** re-runs the check.

---

### Under the hood

- **Periods** are built by `RegressionWindows` (`SqlVitals/Engine/Regressions/`). *The 7 days before* = `[recentStart − 7d, recentStart)`. *The same time last week* = the recent window shifted back 7 days.
- **Detection** is `QueryRegressionDetector` with `RegressionCriteria` (threshold %, minimum executions, 1 ms floor). A query must meet the minimum executions in **both** periods. 
- **Query Store path:** `QueryRegressionRepository` reads `sys.query_store_runtime_stats` joined to `_runtime_stats_interval`, `_plan` and `_query`. Intervals are selected by their **start time**, so a period counts whole Query Store intervals. The "most used plan" in each period is the plan with the most executions. Before and Now plans come from `sys.query_store_plan`. The query timeout is raised to 120 s.
- **History path:** reads `query_deltas` (per-interval changes of `sys.dm_exec_query_stats` totals grouped by `query_hash`) from the [history database](Monitoring-History-Database.md). The Now plan comes from `sys.dm_exec_query_plan` for that `query_hash`.
- **Averages** are total duration (or CPU) ÷ total executions in each period, so they're weighted by executions.
- **Extra time** = `(avgNow − avgBefore) × runsNow`, computed for each metric that regressed. The larger of duration and CPU is used. Rows are ordered by it, capped at 200.
- The threshold and minimum executions are saved per Windows user in `settings.dat` and apply to all connections.
