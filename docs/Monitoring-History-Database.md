# Monitoring History Database

*Technical reference.* For how to **use** history in the app, see [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md).

SqlVitals saves every monitored connection to a local **SQLite** file, with an [activity log](#activity_log) of when monitoring started and stopped and what was changed. You can open it with any SQLite tool (DB Browser for SQLite, the `sqlite3` CLI, DuckDB, Python) for your own analysis.

| | |
|---|---|
| **Location** | `%LocalAppData%\SqlVitals\history.db` (plus `history.db-wal` / `-shm` while in use) |
| **Why local, not roaming** | The file grows, and SQLite's WAL mode doesn't work on the network shares roaming profiles are often redirected to |
| **Why SQLite** | Recorded on [issue #28](https://github.com/aselasampath/sqlvitals/issues/28) (vs DuckDB, LiteDB, flat files) |
| **Schema version** | `PRAGMA user_version`: 1 from v0.28, 2 from v0.30 (adds `counter_values`), 3 from v0.34 (adds `alerts`), 4 from v0.46 (adds `activity_log`), **5** from v0.53 (adds `health_scores`) |

## What's recorded

| Tier | Written | Source on the server | Tables |
|---|---|---|---|
| **Samples** | Every Live Metrics interval (10 s default) | The Live Metrics query itself; **no extra server query** | `metric_samples` |
| **Health scores** | Every collection, including ones that failed | The [health score](Health-Dot-and-Background-Monitoring.md#health-score) as it was graded at the time; **no server query** | `health_scores` |
| **Alerts** | When an [alert](Alerts.md) starts, escalates, gets worse or ends | Worked out from the samples; **no server query** | `alerts` |
| **Activity log** | When monitoring starts or stops, SqlVitals starts or exits, a setting or connection changes, or history is cleared | Nothing on the server | `activity_log` |
| **Detail snapshots** | Every 5 min by default (1 / 5 / 15 / 30) | `sys.dm_os_wait_stats`, `sys.dm_io_virtual_file_stats`, `sys.dm_exec_query_stats` (top 20 by CPU, grouped by `query_hash`), memory counters, `sys.dm_os_performance_counters` | `detail_snapshots` + `wait_deltas`, `file_io_deltas`, `query_deltas`, `query_texts`, `counter_values` |

Detail tables hold the **change over the interval** (deltas), not the server's cumulative totals.

## Schema

```text
connections ─┬─< metric_samples
             ├─< health_scores
             ├─< detail_snapshots ─┬─< wait_deltas
             │                     ├─< file_io_deltas
             │                     ├─< query_deltas >── query_texts (by connection_id + query_hash)
             │                     └─< counter_values
             ├─< query_texts
             └─< alerts

activity_log   (on its own: it copies the connection's name in)
```

| Table | Key columns | Notes |
|---|---|---|
| `connections` | `connection_id` (text), `name`, `server`, `database_name`, `last_seen_utc` | **Never credentials.** Kept after a connection is deleted in the app, so its history stays readable. |
| `metric_samples` | `connection_id`, `captured_utc`, `server_time`, `sql_cpu_pct`, `wait_{cpu,io,lock,memory,network,other}_ms_per_sec`, `batch_requests_per_sec`, `compilations_per_sec`, `recompilations_per_sec`, `transactions_per_sec`, `physical_reads_per_sec`, `physical_writes_per_sec`, `page_life_expectancy_sec`, `memory_grants_pending`, `buffer_cache_hit_ratio` | One row per sample |
| `health_scores` | `connection_id`, `captured_utc`, `score`, `reachable` | One row per collection: the 0–100 score on the thresholds in force then, or 0 with `reachable` = 0 when the server couldn't be reached or read. Stored as graded because the samples don't keep every figure it's graded on (blocking, log and TempDB space). Read by [Your Servers](Your-Servers.md). |
| `detail_snapshots` | `snapshot_id` (PK), `connection_id`, `captured_utc`, `server_time`, `interval_sec`, `total_server_memory_kb`, `target_server_memory_kb`, `database_cache_memory_kb`, `sql_cache_memory_kb`, `granted_workspace_memory_kb`, `free_memory_kb`, `memory_grants_outstanding` | One row per snapshot; memory is "as it stood" |
| `wait_deltas` | `snapshot_id`, `wait_type`, `waiting_tasks`, `wait_time_ms`, `signal_wait_time_ms` | Idle waits excluded |
| `file_io_deltas` | `snapshot_id`, `database_name`, `file_id`, `file_name`, `file_type`, `reads`, `bytes_read`, `read_stall_ms`, `writes`, `bytes_written`, `write_stall_ms` | Stall ÷ count = average latency |
| `query_deltas` | `snapshot_id`, `query_hash`, `executions`, `worker_time_us`, `elapsed_time_us`, `logical_reads`, `logical_writes` | Top 20 by CPU per snapshot |
| `query_texts` | `connection_id`, `query_hash`, `database_name`, `query_text`, `last_seen_utc` | Stored **once** per hash per connection, cut to 4,000 characters |
| `counter_values` | `snapshot_id`, `counter_name`, `value` | Per-second counters as rates over the interval; others as they stood. Zeros not stored. |
| `alerts` | `alert_id` (PK, text), `connection_id`, `indicator`, `severity`, `started_utc`, `ended_utc`, `value`, `threshold`, `detail`, `end_reason` | One row per alert, updated in place as it changes. See [below](#alerts). |
| `activity_log` | `event_id` (PK), `time_utc`, `event`, `connection_id`, `connection_name`, `source`, `setting`, `old_value`, `new_value`, `windows_user`, `machine`, `detail` | One row per event. **Never secrets.** Kept 365 days, and by **Clear history**. See [below](#activity_log). |

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

**The lowest health score per hour today, and whether the server was ever unreachable:**

```sql
SELECT strftime('%H:00', h.captured_utc / 1000, 'unixepoch', 'localtime') AS hour_local,
       MIN(h.score)                                                       AS lowest_score,
       MIN(h.reachable) = 0                                               AS was_unreachable
FROM health_scores h
JOIN connections c USING (connection_id)
WHERE c.name = 'Production'
  AND h.captured_utc > strftime('%s', 'now', 'localtime', 'start of day', 'utc') * 1000
GROUP BY hour_local
ORDER BY hour_local;
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

**When was monitoring stopped last week, and by whom?**

```sql
SELECT datetime(time_utc / 1000, 'unixepoch', 'localtime') AS at_local,
       connection_name, source, windows_user, machine, detail
FROM activity_log
WHERE event = 'Monitoring stopped'
  AND time_utc > (strftime('%s', 'now') - 7 * 86400) * 1000
ORDER BY time_utc DESC;
```

**Why is there a gap in the history?** Every start, stop, app start and exit around a time:

```sql
SELECT datetime(time_utc / 1000, 'unixepoch', 'localtime') AS at_local,
       event, connection_name, source, detail
FROM activity_log
WHERE event IN ('App started', 'App exited', 'Monitoring started', 'Monitoring stopped', 'History cleared')
  AND time_utc BETWEEN (strftime('%s', '2026-09-28 18:00', 'utc')) * 1000
                   AND (strftime('%s', '2026-09-29 09:00', 'utc')) * 1000
ORDER BY time_utc;
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
| `end_reason` | `Recovered`, or `MonitoringStopped` (stopped by the user or by turning off Start monitoring automatically, connection edited or removed, app exited, or left active by a run that was killed; see [Alerts](Alerts.md#when-monitoring-stops)) |

- **Retention:** an alert is purged with the rest of the history, counted from when it **ended**. An active alert is never purged.
- **Left active by a crash:** at startup, before anything else is written, SqlVitals ends every alert still active in the file at the last `metric_samples` row of its connection since it started.

## activity_log

The table behind **⚙️ Settings → [Activity Log](Settings.md#activity-log)**, from schema version 4.

| Column | Holds |
|---|---|
| `event_id` | Row id, in the order written |
| `time_utc` | When it happened: UTC epoch ms on this PC, like the other tables |
| `event` | `App started`, `App exited`, `Monitoring started`, `Monitoring stopped`, `Setting changed`, `Connection added`, `Connection edited`, `Connection removed` or `History cleared` |
| `connection_id` / `connection_name` | The connection it's about; NULL for app-wide events. The name is copied in, so the row still reads correctly after the connection is renamed or removed. |
| `source` | Who or what caused it: `user: Live Metrics`, `user: connection selector`, `user: tray menu`, `user: Settings` (or another page, e.g. `user: Backups`), `automatic at startup`, `setting changed`, `connection edited`, `app exit` |
| `setting` / `old_value` / `new_value` | For a settings change: which setting (or connection field) and its value before and after. For a secret both values are NULL and `detail` is `changed`. |
| `windows_user` / `machine` | The Windows account (`DOMAIN\user`) and PC name, so a shared or copied file still says who did it |
| `detail` | Anything else, in plain words, e.g. *The previous run didn't exit cleanly…* on the `App started` after a run that was killed |

- **Never logged:** passwords, connection strings, a connection's *Additional parameters*, access tokens, webhook URLs. Settings changes are found by comparing the whole settings file before and after each save, in one place, against a fixed list of secret names.
- **A value that didn't change isn't logged.**
- **Retention:** 365 days, separate from the history's; purged hourly with it.
- **Clear history** keeps it and adds a `History cleared` row.
- **Written like everything else:** through the writer's queue, never blocking the app. A row that can't be saved (the file can't be opened or written, or the queue overflowed) goes to the app log instead.
- A **damaged** `history.db` is renamed and a new one started, so the log starts again with it.

## How it's written

- **Never breaks live monitoring.** Samples go into a **bounded in-memory queue**; one background thread (`HistoryWriter`, shared by all connections) writes them in batches. If the disk is slow, the **oldest queued records are dropped**. Failures are logged (at most one line a minute) and never change a health dot.
- **Deltas** (`HistoryDetailTracker`) turn cumulative DMV totals into per-interval changes. An interval is **skipped** when the server restarted (counters reset). A query is left out when its earlier totals are unknown. The first detail read of a session is only the baseline.
- **The first sample** of each session isn't stored: its rates are zero, with nothing to compare against. Its health score is: the health gauges don't need a previous sample.
- **Retention:** older rows are purged **hourly**, and straight away when retention is shortened. `auto_vacuum = INCREMENTAL` plus `incremental_vacuum` and a WAL checkpoint shrink the file as it goes.
- **Clear history** runs on the writer's own thread, including records still queued, so it's safe while monitoring continues and while another SqlVitals window uses the same file. It empties every table except `activity_log`.
- **Settings:** `journal_mode = WAL`, `synchronous = NORMAL`, `busy_timeout = 5000`.
- **Corruption:** a damaged file is renamed `history.corrupt-<timestamp>.db` and a new one is started.
- **Versioning:** a newer SqlVitals upgrades an older file in place and keeps its data. An older SqlVitals finding a **newer** file turns history off rather than changing it.
- The ad-hoc Live Metrics session (no saved connection) is not recorded.

## Code map

| File (`SqlVitals/Engine/History/`) | Role |
|---|---|
| `HistoryStore.cs` | Schema, writes, purge, corrupt-file recovery |
| `HistoryWriter.cs` | Bounded queue + writer thread shared by all connections |
| `HistoryRecorder.cs` | Per connection: queues samples and health scores, takes detail snapshots |
| `HistoryDetailTracker.cs` | Cumulative totals → per-interval deltas |
| `HistoryReader.cs` | Read-only: ranges for trend pages, averaged into buckets |
| `HistoryRange.cs`, `HistoryGaps.cs` | Range choices and bucket sizes; where a line breaks |
| `HistoryBaseline.cs` | The same time last week, lined up under current data |
| `../Alerts/AlertTracker.cs` | Per connection: turns graded samples into alerts, saved as `AlertRecord`s |
| `../Activity/ActivityEntry.cs`, `../Activity/SettingsDiff.cs` | Activity log rows (`ActivityRecord`), their events and sources; one save of the settings turned into rows, secrets left out |
| `Repositories/HistoryRepository.cs` | The server-side reads for the detail snapshot |

## Privacy

`query_texts` can contain **literal values** from your queries and **isn't encrypted**. Treat the file like the app log: don't share it without checking, and delete it with **Settings → Clear history** if needed. See [Security and Privacy](Security-and-Privacy.md).
