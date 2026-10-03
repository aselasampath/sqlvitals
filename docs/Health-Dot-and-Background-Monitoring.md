# Health Dot and Background Monitoring

Every saved connection has a **health dot** and a **health score**, on its tile on **All servers**, in the sidebar's header for the server you've opened, and in the breadcrumb's server menu. They tell you at a glance whether each of your servers is fine, and which one needs attention first, without opening them one by one.

> **Where:** **All servers** (a tile per server), the sidebar's server header, the breadcrumb; the score is also at the top of 💓 Live Metrics · **Configure:** ⚙️ Settings → *Health thresholds* · **Score since:** 0.36.0

## What the colours mean

| Dot | Meaning |
|---|---|
| 🟢 | Healthy. Every indicator is inside its limits. |
| 🟠 | Warning. At least one indicator passed its **warning** threshold. |
| 🔴 | Critical. An indicator passed its **critical** threshold, or the server can't be reached. |
| ◯ | Not monitored. Hover it to see why: monitoring is off, you stopped it, or its password or sign-in is missing. |

**Hover the dot** to see which indicator caused the colour and its current value, or why the connection isn't monitored.

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

The dot says *whether* a server has a problem. The **health score** says *how much*: a number from **0 to 100** beside the open server's name in the sidebar and the breadcrumb, at the top of the **💓 Live Metrics** dashboard, and on each connection's tile on **[All servers](Your-Servers.md)**, which puts the servers that need attention first. With several servers, the lowest score is the one to look at first.

**Click the score** to see which checks lowered it. A panel lists every check with its current figure, its warning and critical thresholds, its weight and the points it cost. The checks that cost points are in bold. On **All servers**, a tile's **⋯ → Why this score…** opens the panel for that server without opening it.

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
- From 0.53.0 every score is saved to the [history](Monitoring-History-Database.md) as it was graded, failed collections as 0, so a server's Summary on [All servers](Your-Servers.md#score-over-time) chart it over the last hour, day or week.
- On Live Metrics it always shows the score **now**, also while the charts show a past range.
- Change a threshold and the score updates straight away, without waiting for the next sample.
- A connection that isn't monitored has no score, in the sidebar, on its tile or on Live Metrics: an old score would read as the server's health now.
- The weights are fixed. To change what counts as a problem, change the [thresholds](#change-the-thresholds), for all connections or [just one](#per-connection-thresholds).

## Change the thresholds

1. Go to **⚙️ Settings → Health thresholds**.
2. Each check has a **Warning** box and a **Critical** box, in the same table as a server's checks. **Leave a box blank to turn that level off.**
3. Click **Save** in the bar at the bottom. The dots and scores update straight away, and [alerts](Alerts.md) use the new values from the next sample. **Reset to defaults** puts SqlVitals' own values back in the boxes; click Save to use them.

The critical value must be past the warning one: higher, or lower for page life expectancy.

### Per-connection thresholds

Some servers always run hot, such as a reporting server that sits at 85 % CPU all night. To stop it showing 🟠 all the time without changing every other server:

1. Open that connection's **Health thresholds** tab: **⚙️ Settings → Connections → Edit**, or **Change** under its checks on [Home](Your-Servers.md#checks).
2. Turn on **Use its own health thresholds** and enter its values. While you type, the table grades its latest sample on them: *On the latest sample, UAT would score 70 → 80*.
3. Click **Save**. The tab shows an **OWN** tag from then on.

Other connections keep using the shared thresholds.

## Starting and stopping monitoring

**Monitoring** means collecting Live Metrics every few seconds, saving [history](History-Time-Ranges-and-Baselines.md) and raising [alerts](Alerts.md). A connection that isn't monitored gets none of it: no queries go to its server except the ones of the pages you open. From 0.46.0 nothing is monitored until you ask for it.

> **Configure:** ⚙️ Settings → *Monitoring & history* → **Start monitoring automatically** (off for a new installation) · **Since:** 0.46.0

### Start monitoring automatically

| Setting | When SqlVitals starts or connects |
|---|---|
| **Off** (new installations) | Nothing is monitored. Live Metrics says *Monitoring is off* and has a **▶ Start monitoring** button. |
| **On** | The active connection, and every connection with **Monitor in background** ticked, are monitored from the start, as SqlVitals did before 0.46.0. |

An installation upgraded from an earlier version keeps monitoring **on**, so nobody silently loses alerts they rely on. Changing the setting applies straight away: turning it on starts the active connection and the background ones; turning it off stops every connection (their active alerts end, as they do on **Stop**).

**Monitor in background** (per connection, on its *Monitoring & alerts* tab in Settings) only matters with the setting on: it keeps a connection monitored while another one is active. It's off for a new connection; saved connections keep their value.

### Start and stop any time

Whatever the setting, you can start and stop monitoring whenever you like:

| Where | What it does |
|---|---|
| **💓 Live Metrics** → **▶ Start monitoring** / **■ Stop monitoring** | The active connection. |
| The sidebar's server header: **▶** / **■** | The server you've opened. |
| **All servers**: a tile's **▶ Start monitoring**, or **⋯ → Start monitoring** / **Stop monitoring** | That server, without opening it. |
| The SqlVitals icon's menu in the notification area: **Start monitoring all** / **Stop monitoring all** | Start: the active connection and those with Monitor in background. Stop: every connection being monitored. |

- **Start** takes the first sample straight away. **Stop** ends that connection's active [alerts](Alerts.md#when-monitoring-stops).
- Every place shows the same state: press ■ in the sidebar and the Live Metrics button, the dot, the tile and the tray tooltip change at once.
- Start and Stop last **until SqlVitals exits**. The next start follows **Start monitoring automatically** again.
- A connection you started keeps running when you switch to another one, like a background connection.
- Hover the server's header in the sidebar, or its name in the breadcrumb, for its state in words: *Monitoring*, *Stopped by you at 10:42*, *Not monitored: the password isn't saved…*
- **▶ is greyed out** for a connection that can't be monitored yet: a SQL login without a saved password, or an Entra MFA connection you haven't signed in to this session. Its tooltip says why. This avoids surprise password or sign-in prompts.

Every start and stop, with who or what caused it, is written to the [activity log](Settings.md#activity-log), so a gap in the history can be explained later.

### While a connection is monitored

- its **health dot and score stay current**;
- when you switch back, **its charts already show** what happened while you were away: the last 60 samples, about 10 minutes at the default 10-second interval;
- its **history** keeps filling, so you can look back later. See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md);
- its **alerts** are raised. A connection that isn't monitored raises none, and the [Alerts](Alerts.md) page says so.

Good to know:

- The **Sample every** interval on the Live Metrics page sets that connection's collection interval, including while it runs in the background. It's kept while the connection is stopped.
- A server that **can't be reached** is retried less and less often, up to every 5 minutes, so it isn't hammered.
---

### Under the hood

- **Sources:** CPU comes from `sys.dm_os_ring_buffers` (scheduler monitor). On Azure SQL Database, which has no scheduler monitor records, it is the latest 15-second `avg_cpu_percent` in `sys.dm_db_resource_stats`: the database's CPU as a share of its service tier's limit, the figure the Azure portal shows as *CPU percentage* (before 0.36.0 it read 0 % there). Blocking is the longest `wait_time` of a blocked request in `sys.dm_exec_requests`. Page life expectancy and memory grants pending are Buffer Manager and Memory Manager counters. Log space is the maximum `Percent Log Used` across databases. TempDB is `Free Space in tempdb` against `Data File(s) Size`. All come from `sys.dm_os_performance_counters`.
- **No extra query:** blocking, log and TempDB are read by the same Live Metrics query that feeds the charts. Their figures aren't saved to history, but [alerts](Alerts.md) on them are.
- **Missing counters:** an indicator the server doesn't report (some counters are missing on certain Azure SQL tiers) is left out of the grade, not flagged.
- **Validation:** values out of range in a hand-edited `settings.dat` fall back to the defaults.
- **Code:** thresholds and grading are in `SqlVitals/Engine/Monitoring/` (`HealthThresholds`, `HealthRules`, `LiveMetricSample`). `HealthRules.Grade` grades each indicator separately; `Evaluate` (the dot) and the [alert tracker](Alerts.md#under-the-hood) both use it. Collectors are run by `Desktop/Services/MonitoringManager.cs`, one `MonitoringSession` per monitored connection. The editor is `Desktop/Controls/HealthThresholdsEditor`.
- **Which connections are monitored:** `MonitoringPlan.Decide` in `Engine/Monitoring/MonitoringPlan.cs` decides, from Start monitoring automatically, the active connection, Monitor in background, the Start and Stop pressed this run (`MonitoringOverrides`), a missing password and the Entra sign-in. It's a pure function, unit tested in `Engine.Tests/Monitoring/MonitoringPlanTests.cs`. `MonitoringManager.Sync` only starts and stops sessions to match it. The upgrade rule is `MonitoringPlan.StartAutomatically`: `StartMonitoringAutomatically` is nullable in `settings.dat`, so a file without it but with connections reads as on. `Desktop.Tests/MonitoringManagerTests.cs` proves, with a repository that counts calls, that with the setting off neither a start nor a connection switch sends a query.
- **Score:** `SqlVitals/Engine/Monitoring/HealthScore.cs`. `HealthScore.From(readings, time)` turns the readings of `HealthRules.Grade` into the score and its checks; `HealthScore.Unavailable` is the 0 for a failed collection. `MonitoringSession.Score` holds the latest one. The panel is `Desktop/Controls/HealthScoreBreakdown`, shown in a popup by the sidebar (`MainWindow`) and by Live Metrics. Unit tested in `Engine.Tests/Monitoring/HealthScoreTests.cs`. Scores aren't saved to the history.
- **Backoff:** unreachable servers retry with exponential backoff, capped at 5 minutes.
