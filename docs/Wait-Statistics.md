# Wait Statistics

Whenever a query can't continue, for example because it needs data from disk, a lock another query holds, or a free CPU, SQL Server records **what it waited for** and **for how long**. Adding those waits up is the fastest way to learn **why** a server is slow.

| Screen | Answers |
|---|---|
| [📊 Top Waits](#top-waits) | What has the server waited on most since it started? |
| [📈 Wait Trend](#wait-trend) | How do selected waits change over time? |

---

## Top Waits

> **Where:** sidebar → 📊 Top Waits · **Use it when:** you're starting any "why is it slow?" investigation

The top 25 wait types, ranked, with a bar chart of the top 15.

| Column | Meaning |
|---|---|
| **Rank** | 1 = the most time spent waiting |
| **Wait Type** | SQL Server's name for the wait, e.g. `PAGEIOLATCH_SH` |
| **Category** | The group it belongs to. See [Wait categories](#wait-categories). |
| **Wait (sec)** | Total time spent on this wait since the server last started |
| **Avg ms/task** | Average length of one wait. Long individual waits matter more than many tiny ones. |
| **Tasks** | How many times it happened |
| **% of Total** / **Cumul %** | Its share of all waits, and the running total down the list |

### Reading it

- **Look at the top 3–5 rows.** They usually cover most of the waiting (watch *Cumul %*).
- The numbers **accumulate since SQL Server last started**. On a server that has run for months, one bad night won't move them much. Use [Wait Trend](#wait-trend) or a history range to see *recent* waits.
- Idle background waits, which are normal, are left out of the ranking.

---

## Wait Trend

> **Where:** sidebar → 📈 Wait Trend · **Use it when:** you want to see *when* a wait rose, or watch a few waits live

### How to use it

1. In **Select Wait Types** on the left, click the wait types to chart. **Filter wait types…** searches the list. **Top Waits** selects the top 20. **Clear All** empties the selection.
2. Pick a **Metric**:
   - **Wait Time (ms/sec)**: milliseconds of waiting per second. The usual choice.
   - **Signal Wait (ms/sec)**: time spent waiting for a CPU *after* the resource became free. High values point to CPU pressure.
   - **Resource Wait (ms/sec)**: time spent waiting for the resource itself: disk, lock, memory.
   - **Waiting Tasks/sec**: how often the wait happens.
3. Choose an **Interval** (10s, 30s or 60s) and press **▶ Start**.
4. To look back instead, use the **range picker** in the top bar: **1h**, **24h**, **7d** or **Custom…**. Add **Compare to baseline** to overlay the same time last week. See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md).

---

## Wait categories

SqlVitals sorts wait types into a few categories, used in Top Waits, Active Waits, the Live charts stacked chart and the Overview's waits chart.

| Category | Usually means | Typical wait types | Where to look next |
|---|---|---|---|
| **CPU** | Queries are competing for processors, or running in parallel | `SOS_SCHEDULER_YIELD`, `CXPACKET`, `CXCONSUMER`, `THREADPOOL` | [Resource Queries](Query-Performance.md#resource-queries) by CPU; *MAXDOP* and *Cost threshold* on [Configuration Checks](Configuration-Checks.md) |
| **I/O** | Reading data from disk, or writing the transaction log | `PAGEIOLATCH_SH/EX`, `WRITELOG`, `IO_COMPLETION` | [Resource Queries](Query-Performance.md#resource-queries) by reads, [Missing Indexes](Index-and-Statistics-Maintenance.md#missing-indexes), storage latency per file on [File I/O](File-IO.md) |
| **Memory** | Waiting for memory, or for pages in memory (latches) | `RESOURCE_SEMAPHORE`, `PAGELATCH_*`, `LATCH_*` | [Memory Grants](Server-Resources.md#memory-grants), [TempDB](Server-Resources.md#tempdb) |
| **Locks** | Blocking: one query waits for another's lock | `LCK_M_S`, `LCK_M_X`, `LCK_M_U`, … | [Processes](Live-Monitoring.md#processes-blocking-chains) |
| **Network** | The client isn't reading results fast enough | `ASYNC_NETWORK_IO` | The application: it may be fetching huge result sets or processing rows one by one |
| **Benign/Idle** | Background tasks sleeping. **Normal; ignore.** | `SLEEP_TASK`, `WAITFOR`, `XE_TIMER_EVENT`, … | — |
| **Other** | Everything else | | Search the wait type name online |

### Signal vs resource waits

Each wait has two parts: the **resource** part (waiting for the disk, lock or memory) and the **signal** part (the resource is ready, but the task is waiting for a CPU). Across the whole server:

| Signal share of all waits | CPU pressure |
|---|---|
| ≤ 10 % | Normal |
| > 10 % | Moderate. Monitor it. |
| > 25 % | High. Threads are queuing for CPU. |

This figure appears in the [Export for AI](Export-for-AI.md) report (*Signal vs Resource Wait*).

---

### Under the hood

- **Source:** `sys.dm_os_wait_stats`, which is cumulative since the last restart or since someone cleared it with `DBCC SQLPERF('sys.dm_os_wait_stats', CLEAR)`.
- **Categories** are assigned in SQL by a `CASE` on `wait_type` in `WaitStatsRepository.GetCumulativeWaitsAsync`. The Benign/Idle list is also excluded from the *% of Total* denominator. Category colours come from `CategoryColor()`.
- **Wait Trend live** computes per-second rates from the difference between successive reads. **Wait Trend history** reads the `wait_deltas` table, which holds per-type changes per detail snapshot (5 minutes by default). Idle waits are not stored there. For that reason, a past range starts on the range's own top waits if none of your selected types were recorded.
- The Live charts stacked chart (and the Overview's waits chart) plots per-category wait ms/sec from the live sample (`metric_samples`), not per type.
