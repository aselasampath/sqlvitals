# Monitoring History Database

*Technical reference.* For how to **use** history in the app, see [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md).

SqlVitals saves every background-monitored connection to a local **SQLite** file. You can open it with any SQLite tool (DB Browser for SQLite, the `sqlite3` CLI, DuckDB, Python) for your own analysis.

| | |
|---|---|
| **Location** | `%LocalAppData%\SqlVitals\history.db` (plus `history.db-wal` / `-shm` while in use) |
| **Why local, not roaming** | The file grows, and SQLite's WAL mode doesn't work on the network shares roaming profiles are often redirected to |
| **Why SQLite** | Recorded on [issue #28](https://github.com/aselasampath/sqlvitals/issues/28) (vs DuckDB, LiteDB, flat files) |
| **Schema version** | `PRAGMA user_version`: 1 from v0.28, 2 from v0.30 (adds `counter_values`), **3** from v0.34 (adds `alerts`) |

## What's recorded

| Tier | Written | Source on the server | Tables |
|---|---|---|---|
| **Samples** | Every Live Metrics interval (10 s default) | The Live Metrics query itself; **no extra server query** | `metric_samples` |
| **Alerts** | When an [alert](Alerts.md) starts, escalates, gets worse or ends | Worked out from the samples; **no server query** | `alerts` |
| **Detail snapshots** | Every 5 min by default (1 / 5 / 15 / 30) | `sys.dm_os_wait_stats`, `sys.dm_io_virtual_file_stats`, `sys.dm_exec_query_stats` (top 20 by CPU, grouped by `query_hash`), memory counters, `sys.dm_os_performance_counters` | `detail_snapshots` + `wait_deltas`, `file_io_deltas`, `query_deltas`, `query_texts`, `counter_values` |

Detail tables hold the **change over the interval** (deltas), not the server's cumulative totals.

## Schema

```text
connections ─┬─< metric_samples
             ├─< detail_snapshots ─┬─< wait_deltas
             │                     ├─< file_io_deltas
             │                     ├─< query_deltas >── query_texts (by connection_id + query_hash)
             │                     └─< counter_values
             ├─< query_texts
             └─< alerts
```

| Table | Key columns | Notes |
|---|---|---|
| `connections` | `connection_id` (text), `name`, `server`, `database_name`, `last_seen_utc` | **Never credentials.** Kept after a connection is deleted in the app, so its history stays readable. |
| `metric_samples` | `connection_id`, `captured_utc`, `server_time`, `sql_cpu_pct`, `wait_{cpu,io,lock,memory,network,other}_ms_per_sec`, `batch_requests_per_sec`, `compilations_per_sec`, `recompilations_per_sec`, `transactions_per_sec`, `physical_reads_per_sec`, `physical_writes_per_sec`, `page_life_expectancy_sec`, `memory_grants_pending`, `buffer_cache_hit_ratio` | One row per sample |
| `detail_snapshots` | `snapshot_id` (PK), `connection_id`, `captured_utc`, `server_time`, `interval_sec`, `total_server_memory_kb`, `target_server_memory_kb`, `database_cache_memory_kb`, `sql_cache_memory_kb`, `granted_workspace_memory_kb`, `free_memory_kb`, `memory_grants_outstanding` | One row per snapshot; memory is "as it stood" |
| `wait_deltas` | `snapshot_id`, `wait_type`, `waiting_tasks`, `wait_time_ms`, `signal_wait_time_ms` | Idle waits excluded |
| `file_io_deltas` | `snapshot_id`, `database_name`, `file_id`, `file_name`, `file_type`, `reads`, `bytes_read`, `read_stall_ms`, `writes`, `bytes_written`, `write_stall_ms` | Stall ÷ count = average latency |
| `query_deltas` | `snapshot_id`, `query_hash`, `executions`, `worker_time_us`, `elapsed_time_us`, `logical_reads`, `logical_writes` | Top 20 by CPU per snapshot |
| `query_texts` | `connection_id`, `query_hash`, `database_name`, `query_text`, `last_seen_utc` | Stored **once** per hash per connection, cut to 4,000 characters |
| `counter_values` | `snapshot_id`, `counter_name`, `value` | Per-second counters as rates over the interval; others as they stood. Zeros not stored. |
| `alerts` | `alert_id` (PK, text), `connection_id`, `indicator`, `severity`, `started_utc`, `ended_utc`, `value`, `threshold`, `detail`, `end_reason` | One row per alert, updated in place as it changes. See [below](#alerts). |

**Times:** `captured_utc` is **UTC epoch milliseconds from this PC**. `server_time` is the server's own clock, as text.

## Example queries

Open the file **read-only** while SqlVitals is running (WAL mode allows it), or copy it first:

```powershell
sqlite3 "file:$env:LOCALAPPDATA\SqlVitals\history.db?mode=ro"
```

**CPU over the last 24 hours for one connection:**

```sql
SELECT datetime(m.captured_utc / 1000, 'unixepoch', 'localtime') AS at_local,
       m.sql_cpu_pct
FROM metric_samples m
JOIN connections c USING (connection_id)
WHERE c.name = 'Production'
  AND m.captured_utc > (strftime('%s', 'now') - 86400) * 1000
ORDER BY m.captured_utc;
```

**Top waits in the last 24 hours:**

```sql
SELECT w.wait_type,
       ROUND(SUM(w.wait_time_ms) / 1000.0, 1) AS wait_sec,
       ROUND(100.0 * SUM(w.signal_wait_time_ms) / NULLIF(SUM(w.wait_time_ms), 0), 1) AS signal_pct
FROM wait_deltas w
JOIN detail_snapshots s USING (snapshot_id)
JOIN connections c ON c.connection_id = s.connection_id
WHERE c.name = 'Production'
  AND s.captured_utc > (strftime('%s', 'now') - 86400) * 1000
GROUP BY w.wait_type
ORDER BY wait_sec DESC
LIMIT 20;
```

**Top queries by CPU in the last 7 days, with text:**

```sql
SELECT q.query_hash,
       SUM(q.executions)                                        AS executions,
       ROUND(SUM(q.worker_time_us) / 1000.0)                    AS cpu_ms,
       ROUND(SUM(q.elapsed_time_us) / 1000.0 / SUM(q.executions), 2) AS avg_duration_ms,
       substr(t.query_text, 1, 200)                             AS query_text
FROM query_deltas q
JOIN detail_snapshots s USING (snapshot_id)
LEFT JOIN query_texts t ON t.connection_id = s.connection_id AND t.query_hash = q.query_hash
WHERE s.captured_utc > (strftime('%s', 'now') - 7 * 86400) * 1000
GROUP BY s.connection_id, q.query_hash
ORDER BY cpu_ms DESC
LIMIT 20;
```

**Alerts in the last 7 days, longest first:**

```sql
SELECT c.name AS connection, a.indicator, a.severity,
       datetime(a.started_utc / 1000, 'unixepoch', 'localtime') AS started_local,
       datetime(a.ended_utc / 1000, 'unixepoch', 'localtime')   AS ended_local,
       ROUND((COALESCE(a.ended_utc, strftime('%s', 'now') * 1000) - a.started_utc) / 60000.0, 1) AS minutes,
       a.value, a.threshold, a.detail, a.end_reason
FROM alerts a
JOIN connections c USING (connection_id)
WHERE COALESCE(a.ended_utc, strftime('%s', 'now') * 1000) > (strftime('%s', 'now') - 7 * 86400) * 1000
ORDER BY minutes DESC;
```

**Average read and write latency per file:**

```sql
SELECT f.database_name, f.file_name,
       ROUND(1.0 * SUM(f.read_stall_ms)  / NULLIF(SUM(f.reads), 0), 1)  AS avg_read_ms,
       ROUND(1.0 * SUM(f.write_stall_ms) / NULLIF(SUM(f.writes), 0), 1) AS avg_write_ms
FROM file_io_deltas f
JOIN detail_snapshots s USING (snapshot_id)
WHERE s.captured_utc > (strftime('%s', 'now') - 86400) * 1000
GROUP BY f.database_name, f.file_name
ORDER BY avg_read_ms DESC;
```

> Don't write to the file while SqlVitals is running. Treat it as read-only.

## Alerts

The `alerts` table is the timeline behind the [Alerts](Alerts.md) page.

| Column | Meaning |
|---|---|
| `alert_id` | A GUID. A later record of the same alert (worse value, escalation, end) replaces the row. |
| `indicator` | `Cpu`, `Blocking`, `PageLifeExpectancy`, `MemoryGrantsPending`, `LogSpace` or `TempDbSpace` |
| `severity` | `Warning` or `Critical`: the worst level it held for the set number of samples |
| `started_utc` / `ended_utc` | UTC epoch ms on this PC: the first sample past the threshold, and the first back to normal. `ended_utc` is NULL while the alert is active. |
| `value` | The worst figure: the highest, or the lowest for page life expectancy. Blocking in seconds. |
| `threshold` | The threshold of its severity that was crossed |
| `detail` | The health dot's reason text at the worst value, e.g. `Log 93% full (Sales)` |
| `end_reason` | `Recovered`, or `MonitoringStopped` (paused, connection edited or removed, app exited, or left active by a run that was killed; see [Alerts](Alerts.md#when-monitoring-stops)) |

- **Retention:** an alert is purged with the rest of the history, counted from when it **ended**. An active alert is never purged.
- **Left active by a crash:** at startup, before anything else is written, SqlVitals ends every alert still active in the file at the last `metric_samples` row of its connection since it started.

## How it's written

- **Never breaks live monitoring.** Samples go into a **bounded in-memory queue**; one background thread (`HistoryWriter`, shared by all connections) writes them in batches. If the disk is slow, the **oldest queued records are dropped**. Failures are logged (at most one line a minute) and never change a health dot.
- **Deltas** (`HistoryDetailTracker`) turn cumulative DMV totals into per-interval changes. An interval is **skipped** when the server restarted (counters reset). A query is left out when its earlier totals are unknown. The first detail read of a session is only the baseline.
- **The first sample** of each session isn't stored: its rates are zero, with nothing to compare against.
- **Retention:** older rows are purged **hourly**, and straight away when retention is shortened. `auto_vacuum = INCREMENTAL` plus `incremental_vacuum` and a WAL checkpoint shrink the file as it goes.
- **Clear history** runs on the writer's own thread, including records still queued, so it's safe while monitoring continues and while another SqlVitals window uses the same file.
- **Settings:** `journal_mode = WAL`, `synchronous = NORMAL`, `busy_timeout = 5000`.
- **Corruption:** a damaged file is renamed `history.corrupt-<timestamp>.db` and a new one is started.
- **Versioning:** a newer SqlVitals upgrades an older file in place and keeps its data. An older SqlVitals finding a **newer** file turns history off rather than changing it.
- The ad-hoc Live Metrics session (no saved connection) is not recorded.

## Code map

| File (`SqlVitals/Engine/History/`) | Role |
|---|---|
| `HistoryStore.cs` | Schema, writes, purge, corrupt-file recovery |
| `HistoryWriter.cs` | Bounded queue + writer thread shared by all connections |
| `HistoryRecorder.cs` | Per connection: queues samples, takes detail snapshots |
| `HistoryDetailTracker.cs` | Cumulative totals → per-interval deltas |
| `HistoryReader.cs` | Read-only: ranges for trend pages, averaged into buckets |
| `HistoryRange.cs`, `HistoryGaps.cs` | Range choices and bucket sizes; where a line breaks |
| `HistoryBaseline.cs` | The same time last week, lined up under current data |
| `../Alerts/AlertTracker.cs` | Per connection: turns graded samples into alerts, saved as `AlertRecord`s |
| `Repositories/HistoryRepository.cs` | The server-side reads for the detail snapshot |

## Privacy

`query_texts` can contain **literal values** from your queries and **isn't encrypted**. Treat the file like the app log: don't share it without checking, and delete it with **Settings → Clear history** if needed. See [Security and Privacy](Security-and-Privacy.md).
