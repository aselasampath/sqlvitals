# Health Dot and Background Monitoring

Every saved connection has a **health dot** and a **health score** in the sidebar's connection selector. They tell you at a glance whether each of your servers is fine, and which one needs attention first, without opening them one by one.

> **Where:** sidebar → *Active connection* selector; the score is also at the top of 💓 Live Metrics · **Configure:** ⚙️ Settings → *Health Thresholds* · **Score since:** 0.36.0

## What the colours mean

| Dot | Meaning |
|---|---|
| 🟢 | Healthy. Every indicator is inside its limits. |
| 🟠 | Warning. At least one indicator passed its **warning** threshold. |
| 🔴 | Critical. An indicator passed its **critical** threshold, or the server can't be reached. |
| ◯ | Not monitored, or monitoring is paused. |

**Hover the dot** to see which indicator caused the colour and its current value.

The dot shows the state **now**. When an indicator stays past a threshold for a few samples in a row, SqlVitals also records an **alert**, so you can see later when it started and ended. See [Alerts](Alerts.md).

## What is checked

| Indicator | In plain words | Warning | Critical |
|---|---|---|---|
| **CPU** | How busy SQL Server's processors were over the last minute | ≥ 75 % | ≥ 90 % |
| **Blocking** | How long the longest-blocked query has been stuck waiting for another | ≥ 30 s | ≥ 120 s |
| **Page life expectancy** | How long data stays in memory before it's pushed out. *Lower is worse.* | < 300 s | off |
| **Memory grants pending** | Queries queued because there isn't enough memory to start them | ≥ 1 | off |
| **Log space used** | The fullest transaction log on the server. The tooltip names the database. | ≥ 80 % | ≥ 90 % |
| **TempDB space used** | How full TempDB's data files are | ≥ 80 % | ≥ 90 % |

These are the defaults. **off** means that level isn't checked.

## Health score

The dot says *whether* a server has a problem. The **health score** says *how much*: a number from **0 to 100** beside each connection in the selector, and at the top of the **💓 Live Metrics** dashboard. With several servers, the lowest score is the one to look at first.

**Click the score** to see which checks lowered it. A panel lists every check with its current figure, its warning and critical thresholds, its weight and the points it cost. The checks that cost points are in bold. In the selector, clicking a score opens the panel for that connection without switching to it.

### How it's calculated

The score uses the same checks and thresholds as the dot (the table above), so the two always agree. Each check is worth a share of the 100 points:

| Check | Weight | Why |
|---|---|---|
| **CPU** | 20 | A server out of CPU slows every query |
| **Blocking** | 20 | Blocked queries stop users' work |
| **Log space used** | 20 | A full transaction log stops all writes to that database |
| **Page life expectancy** | 16 | Memory pressure slows things down broadly |
| **Memory grants pending** | 12 | Queries are waiting for memory to start |
| **TempDB space used** | 12 | A full TempDB fails the queries that need it |

- A check inside its thresholds keeps all its points.
- Past its **warning** threshold, it loses **half** its weight.
- Past its **critical** threshold, it loses **all** of it.
- A check the server doesn't report (a counter missing on some Azure SQL tiers) keeps its points, just as it doesn't turn the dot amber.
- A server that **can't be reached** scores **0**. The panel shows the error instead of the checks.

**Examples** with the default thresholds:

| What's happening | Score |
|---|---|
| Everything within its thresholds | **100** |
| CPU at 80 % (warning) | 100 − 10 = **90** |
| Blocking for 3 minutes (critical) and CPU at 80 % (warning) | 100 − 20 − 10 = **70** |
| Blocking critical, page life expectancy low (warning), TempDB 85 % full (warning) | 100 − 20 − 8 − 6 = **66** |

The score's **colour is the dot's colour**, not a banding of the number. A single critical check can leave a score of 80 or more, but it still shows 🔴, because something is critical.

### Good to know

- The score is for the **latest sample**, like the dot, so it moves with every refresh. For how long a problem lasted, see [Alerts](Alerts.md).
- On Live Metrics it always shows the score **now**, also while the charts show a past range.
- Change a threshold and the score updates straight away, without waiting for the next sample.
- While a connection is **paused** its score is dimmed: it's the last score taken, not a current one. A connection that isn't monitored has no score.
- The weights are fixed. To change what counts as a problem, change the [thresholds](#change-the-thresholds), for all connections or [just one](#per-connection-thresholds).

## Change the thresholds

1. Go to **⚙️ Settings → Health Thresholds**.
2. Each indicator has a **Warning** box and a **Critical** box. **Leave a box blank to turn that level off.**
3. Click **💾 Save thresholds**. The dots and scores update straight away, and [alerts](Alerts.md) use the new values from the next sample. **↺ Defaults** puts the default values back in the boxes; click Save to use them.

The critical value must be past the warning one: higher, or lower for page life expectancy.

### Per-connection thresholds

Some servers always run hot, such as a reporting server that sits at 85 % CPU all night. To stop it showing 🟠 all the time without changing every other server:

1. Open that connection in **Settings**.
2. Tick **Use its own health thresholds** and enter its values.
3. Click **Save**.

Other connections keep using the shared thresholds.

## Background monitoring

With **Monitor in background** ticked (the default), a connection keeps collecting Live Metrics while you're looking at another one. That means:

- its **health dot and score stay current**;
- when you switch back, **its charts already show** what happened while you were away: the last 60 samples, about 10 minutes at the default 10-second interval;
- its **history** keeps filling, so you can look back later. See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md).

Good to know:

- The **interval** and **Start / Stop** on the Live Metrics page control that connection's collector, including while it runs in the background.
- A server that **can't be reached** is retried less and less often, up to every 5 minutes, so it isn't hammered.
- **Entra MFA** connections, and SQL logins without a saved password, start background collection only after you've switched to them once in the session. This avoids surprise sign-in or password prompts.
- Untick **Monitor in background** for servers you rarely care about. Their dot shows ◯ and they have no score.

---

### Under the hood

- **Sources:** CPU comes from `sys.dm_os_ring_buffers` (scheduler monitor). On Azure SQL Database, which has no scheduler monitor records, it is the latest 15-second `avg_cpu_percent` in `sys.dm_db_resource_stats`: the database's CPU as a share of its service tier's limit, the figure the Azure portal shows as *CPU percentage* (before 0.36.0 it read 0 % there). Blocking is the longest `wait_time` of a blocked request in `sys.dm_exec_requests`. Page life expectancy and memory grants pending are Buffer Manager and Memory Manager counters. Log space is the maximum `Percent Log Used` across databases. TempDB is `Free Space in tempdb` against `Data File(s) Size`. All come from `sys.dm_os_performance_counters`.
- **No extra query:** blocking, log and TempDB are read by the same Live Metrics query that feeds the charts. Their figures aren't saved to history, but [alerts](Alerts.md) on them are.
- **Missing counters:** an indicator the server doesn't report (some counters are missing on certain Azure SQL tiers) is left out of the grade, not flagged.
- **Validation:** values out of range in a hand-edited `settings.dat` fall back to the defaults.
- **Code:** thresholds and grading are in `SqlVitals/Engine/Monitoring/` (`HealthThresholds`, `HealthRules`, `LiveMetricSample`). `HealthRules.Grade` grades each indicator separately; `Evaluate` (the dot) and the [alert tracker](Alerts.md#under-the-hood) both use it. Collectors are run by `Desktop/Services/MonitoringManager.cs`, one `MonitoringSession` per connection. The editor is `Desktop/Controls/HealthThresholdsEditor`.
- **Score:** `SqlVitals/Engine/Monitoring/HealthScore.cs`. `HealthScore.From(readings, time)` turns the readings of `HealthRules.Grade` into the score and its checks; `HealthScore.Unavailable` is the 0 for a failed collection. `MonitoringSession.Score` holds the latest one. The panel is `Desktop/Controls/HealthScoreBreakdown`, shown in a popup by the sidebar (`MainWindow`) and by Live Metrics. Unit tested in `Engine.Tests/Monitoring/HealthScoreTests.cs`. Scores aren't saved to the history.
- **Backoff:** unreachable servers retry with exponential backoff, capped at 5 minutes.
