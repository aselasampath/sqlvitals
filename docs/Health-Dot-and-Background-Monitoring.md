# Health Dot and Background Monitoring

Every saved connection has a **health dot** and a **health score**, on its tile on **All servers**, in the sidebar's header for the server you've opened, and in the breadcrumb's server menu. They tell you at a glance whether each of your servers is fine, and which one needs attention first, without opening them one by one.

> **Where:** **All servers** (a tile per server), the sidebar's server header, the breadcrumb; the score, and what is costing it points, on each server's [Overview](Your-Servers.md#a-servers-overview) · **Configure:** ⚙️ Settings → *Health thresholds* · **Score since:** 0.36.0

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
| **Page life expectancy** | How long data stays in memory before it's pushed out. *Lower is worse.* | < 300 s per 4 GB of buffer pool | off |
| **Memory grants pending** | Queries queued because there isn't enough memory to start them | ≥ 1 | off |
| **Log space used** | The fullest transaction log on the server. The tooltip names the database and what the log is waiting on. | ≥ 80 % | ≥ 90 % |
| **TempDB space used** | How full TempDB's data files are | ≥ 80 % | ≥ 90 % |

These are the defaults. **off** means that level isn't checked.

**Page life expectancy scales with the server (from 0.75.0).** A fixed 300 s fires on a small VM during CHECKDB or an index rebuild, and stays green on a large server that is short of memory. So by default the threshold is **300 s per 4 GB of buffer pool**, the usual guide: a server with 64 GB of buffer pool is warned below 300 × 64 ÷ 4 = **4,800 s**, one with 2 GB below **150 s**. The panel's tooltip says what it was scaled to. If the server doesn't report its buffer pool, the threshold is used as it is. To use a fixed number of seconds instead, turn off **Per 4 GB of buffer pool** under page life expectancy in [Settings](#change-the-thresholds).

**A small log that is full but reusable isn't a warning (from 0.75.0).** A log at 85 % is only a problem if SQL Server can't reuse it. Log space looks at the fullest log's `log_reuse_wait_desc` and size: a log **under 1 GB** whose reuse waits on **nothing** or only the next **checkpoint** is simply sized tight, and will wrap around rather than grow, so it stays green. Any other log is graded as before, and its tooltip says what it's waiting on (*Log 92% full (Sales, waiting on LOG_BACKUP)*). When one log is small and reusable and another is filling up for a reason, the second is the one checked.

## Health score

The dot says *whether* a server has a problem. The **health score** says *how much*: a number from **0 to 100** beside the open server's name in the sidebar and the breadcrumb, on the server's [Overview](Your-Servers.md#a-servers-overview) with what is costing it points, and on each connection's tile on **[All servers](Your-Servers.md)**, which puts the servers that need attention first. With several servers, the lowest score is the one to look at first.

### The worst state first

Wherever the score is shown, it comes **second**. First comes the worst state and its reason, in the dot's colour (from 0.75.0):

| Where | What it says |
|---|---|
| A tile on **All servers** | **Critical: blocking for 4 min**, then *Score 80 / 100* |
| The server's **Overview** | the same line above the number |
| The sidebar's server header | the same line under the server's name |
| **Settings → Connections** | *Critical: blocking for 4 min · score 80* on the connection's card |

When several checks are past a threshold, it names the worst and costliest, and how many more: *Warning: CPU at 82 % (+1 more)*. A healthy server says *Healthy: every check passes*, or *Healthy: 5 of 6 checks pass, 1 has no data* when a check isn't reported. So a score of 80 never sits beside a 🔴 without the reason.

**Click the score** to see which checks lowered it. The panel leads with the same line, then lists every check with its current figure, its warning and critical thresholds, its worth and the points it cost. The checks that cost points are in bold. A check with no data is greyed, with a dash for its points, and a line under the table says how many were left out. On **All servers**, a tile's **⋯ → Why this score…** opens the panel for that server without opening it.

### How it's calculated

The score uses the same checks and thresholds as the dot (the table above), so the two always agree. Each check has a weight; these are the defaults, and you can change them in [Settings](#change-the-weights):

| Check | Weight | Why |
|---|---|---|
| **CPU** | 20 | A server out of CPU slows every query |
| **Blocking** | 20 | Blocked queries stop users' work |
| **Log space used** | 20 | A full transaction log stops all writes to that database |
| **Page life expectancy** | 16 | Memory pressure slows things down broadly |
| **Memory grants pending** | 12 | Queries are waiting for memory to start |
| **TempDB space used** | 12 | A full TempDB fails the queries that need it |

- A check inside its thresholds keeps all its points.
- Past its **warning** threshold, it loses **half** of them.
- Past its **critical** threshold, it loses **all** of them.
- **A check with no data is Unknown, never passing** (from 0.75.0). A counter the server doesn't report (page life expectancy or TempDB on some Azure SQL tiers) shows grey, with no tick, and is **left out** of the score. The score is then shared out over the checks that did report, so it is still out of 100: with TempDB missing, the other five share the 100 points among their 88 of weight, and CPU is worth 20 × 100 ÷ 88 = **23**. Missing data can't make a server look healthier than its checks say. Before 0.75.0 a missing check kept its points and was listed under *Passing*.
- A server that **can't be reached** scores **0**. The panel shows the error instead of the checks.

The panel calls each check's share its **worth**. With every check reporting and the default weights, the worth is the weight.

**Examples** with the default thresholds:

| What's happening | Score |
|---|---|
| Everything within its thresholds | **100** |
| CPU at 80 % (warning) | 100 − 10 = **90** |
| Blocking for 3 minutes (critical) and CPU at 80 % (warning) | 100 − 20 − 10 = **70** |
| Blocking critical, page life expectancy low (warning), TempDB 85 % full (warning) | 100 − 20 − 8 − 6 = **66** |
| CPU at 99 % (critical), TempDB not reported | CPU is worth 23 of 100 among the five that reported: 100 − 23 = **77** |

The score's **colour is the dot's colour**, not a banding of the number. A single critical check can leave a score of 80 or more, but it still shows 🔴, because something is critical.

### Good to know

- The score is for the **latest sample**, like the dot, so it moves with every refresh. For how long a problem lasted, see [Alerts](Alerts.md).
- From 0.53.0 every score is saved to the [history](Monitoring-History-Database.md) as it was graded, failed collections as 0, so a server's [Overview](Your-Servers.md#charts) charts it over the window in the top bar.
- On the Overview it always shows the score **now**, also while the charts show a past range. *What is costing points* lists each check that lowers it with its points, why, and the page that explains it; the points are the score's own, so they add up to what it lost.
- Change a threshold and the score updates straight away, without waiting for the next sample.
- A connection that isn't monitored has no score, in the sidebar, on its tile or on its Overview: an old score would read as the server's health now.
- A server handed to the [collector service](Collector-Service.md) has its score in the history, but not the figures behind it. So on its tile and Overview its checks are **Unknown** too: grey bars, except the checks with an open alert, and its line says what is alerting (*Critical: alerting on CPU*).
- To change what counts as a problem, change the [thresholds](#change-the-thresholds); to change how much each check counts, change the [weights](#change-the-weights). Both apply to all connections or [just one](#per-connection-thresholds).

## Change the thresholds

1. Go to **⚙️ Settings → Health thresholds**.
2. Each check has a **Warning** box and a **Critical** box, in the same table as a server's checks. **Leave a box blank to turn that level off.**
3. Under **Page life expectancy**, **Per 4 GB of buffer pool** is on: its boxes are seconds per 4 GB. Turn it off to use them as a fixed number of seconds.
4. Click **Save** in the bar at the bottom. The dots and scores update straight away, and [alerts](Alerts.md) use the new values from the next sample. **Reset to defaults** puts SqlVitals' own values back in the boxes; click Save to use them.

The critical value must be past the warning one: higher, or lower for page life expectancy.

A settings file from before 0.75.0 keeps its page life expectancy as it was meant: if you had changed it from 300 s, it stays a fixed number of seconds; if not, it now scales with the buffer pool.

### Change the weights

The **Weight** column of the same table sets how much each check counts, from **0 to 100** (from 0.75.0):

- Weights are **relative**: the score is always out of 100, shared out over the checks that reported. Doubling every weight changes nothing.
- **0 leaves a check out of the score.** Its thresholds still colour the dot and raise alerts, so a server can show 🔴 with a score of 100; the line before the score then says why (*Critical: CPU at 99 %*). To stop checking something at all, leave its threshold boxes blank instead.
- At least one check needs a weight above 0.
- While you type, the page previews every monitored connection on these thresholds: *On the latest sample, UAT would score 70 → 80*.

The defaults stay as in the table above. Changes are logged in the [activity log](Settings.md#activity-log) like the thresholds: *Health weight: CPU* 20 → 0, and *Health threshold: Page life expectancy scaling*.

### Per-connection thresholds

Some servers always run hot, such as a reporting server that sits at 85 % CPU all night. To stop it showing 🟠 all the time without changing every other server:

1. Open that connection's **Health thresholds** tab: **⚙️ Settings → Connections → Edit**, or **Change** under its checks on [Home](Your-Servers.md#checks).
2. Turn on **Use its own health thresholds** and enter its values, its weights too. While you type, the table grades its latest sample on them: *On the latest sample, UAT would score 70 → 80*.
3. Click **Save**. The tab shows an **OWN** tag from then on.

Other connections keep using the shared thresholds.

## Starting and stopping monitoring

**Monitoring** means collecting live metrics every few seconds, saving [history](History-Time-Ranges-and-Baselines.md) and raising [alerts](Alerts.md). A connection that isn't monitored gets none of it: no queries go to its server except the ones of the pages you open. From 0.46.0 nothing is monitored until you ask for it.

> **Configure:** ⚙️ Settings → *Monitoring & history* → **Start monitoring automatically** (off for a new installation) · **Since:** 0.46.0

### Start monitoring automatically

| Setting | When SqlVitals starts or connects |
|---|---|
| **Off** (new installations) | Nothing is monitored. A server's Overview says it isn't monitored and has **Start monitoring**; Live charts says *Monitoring is off*. |
| **On** | The active connection, and every connection with **Monitor in background** ticked, are monitored from the start, as SqlVitals did before 0.46.0. |

An installation upgraded from an earlier version keeps monitoring **on**, so nobody silently loses alerts they rely on. Changing the setting applies straight away: turning it on starts the active connection and the background ones; turning it off stops every connection (their active alerts end, as they do on **Stop**).

**Monitor in background** (per connection, on its *Monitoring & alerts* tab in Settings) only matters with the setting on: it keeps a connection monitored while another one is active. It's off for a new connection; saved connections keep their value.

### Start and stop any time

Whatever the setting, you can start and stop monitoring whenever you like:

| Where | What it does |
|---|---|
| A server's **Overview** → **Start monitoring** / **Pause monitoring** | That server. |
| **💓 Live charts** → **▶ Start monitoring** / **■ Stop monitoring** | The active connection. |
| The sidebar's server header: **▶** / **■** | The server you've opened. |
| **All servers**: a tile's **▶ Start monitoring**, or **⋯ → Start monitoring** / **Stop monitoring** | That server, without opening it. |
| The SqlVitals icon's menu in the notification area: **Start monitoring all** / **Stop monitoring all** | Start: the active connection and those with Monitor in background. Stop: every connection being monitored. |

- **Start** takes the first sample straight away. **Stop** ends that connection's active [alerts](Alerts.md#when-monitoring-stops).
- Every place shows the same state: press ■ in the sidebar and the Overview's and Live charts' buttons, the dot, the tile and the tray tooltip change at once.
- Start and Stop last **until SqlVitals exits**. The next start follows **Start monitoring automatically** again.
- A connection you started keeps running when you switch to another one, like a background connection.
- Hover the server's header in the sidebar, or its name in the breadcrumb, for its state in words: *Monitoring*, *Stopped by you at 10:42*, *Not monitored: the password isn't saved…*
- **With the [collector service](Collector-Service.md#from-the-app) installed** (0.74.0), ▶ asks whether the service should watch the server instead: it keeps watching when SqlVitals is closed. A server handed to it isn't collected by the app, and shows *Watched by the SqlVitals Collector service*.
- **▶ is greyed out** for a connection that can't be monitored yet: a SQL login without a saved password, or an Entra MFA connection you haven't signed in to this session. Its tooltip says why. This avoids surprise password or sign-in prompts.

Every start and stop, with who or what caused it, is written to the [activity log](Settings.md#activity-log), so a gap in the history can be explained later.

### When nothing is watching

Monitoring runs **only while SqlVitals runs on this PC**. When the PC sleeps, shuts down or loses power, you sign out, or SqlVitals is closed, nothing is collected and no alert is raised, so a quiet dot or channel isn't proof the servers are fine.

- Every [chat channel](Chat-Notifications.md#stopped-watching-and-watching-again) is told: *⏹ SqlVitals stopped watching Sales-Prod because the PC is going to sleep*, on **Stop**, a setting that stops a connection, removing it, **Exit**, Windows shutting down or signing out, and sleep. The next start (or wake, or **Start**) posts *▶ SqlVitals is watching Sales-Prod again, after 7 h 05 min without monitoring*. Since 0.70.0 ([#170](https://github.com/aselasampath/sqlvitals/issues/170)).
- If SqlVitals is killed or the PC loses power, there's no *stopped* message; the *watching again* message still gives the real gap, from the last sample in the history.
- To be told **even then**, turn on a channel's [heartbeat](Chat-Notifications.md#heartbeat) (every few hours, or daily) and set up an alert for when one doesn't arrive: a dead man's switch service, or for Teams a Power Automate flow. Since 0.71.0.
- **Home and each server's Overview** say how much of the last 24 hours was watched (*Monitored 14 h of the last 24 h*), and the score charts shade the gaps grey: see [How much was monitored](Your-Servers.md#how-much-was-monitored). Since 0.72.0.
- To keep watching while the window is closed, keep SqlVitals [running in the notification area](Notifications-and-Tray.md#running-in-the-notification-area), and stop the PC sleeping while it monitors (Windows **Settings → System → Power**).

### While a connection is monitored

- its **health dot and score stay current**;
- when you switch back, **its charts already show** what happened while you were away: the last 60 samples, about 10 minutes at the default 10-second interval;
- its **history** keeps filling, so you can look back later. See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md);
- its **alerts** are raised. A connection that isn't monitored raises none, and the [Alerts](Alerts.md) page says so.

Good to know:

- The **Sample every** interval on the Live charts page sets that connection's collection interval, including while it runs in the background. It's kept while the connection is stopped.
- A server that **can't be reached** is retried less and less often, up to every 5 minutes, so it isn't hammered.
---

### Under the hood

- **Sources:** CPU comes from `sys.dm_os_ring_buffers` (scheduler monitor). On Azure SQL Database, which has no scheduler monitor records, it is the latest 15-second `avg_cpu_percent` in `sys.dm_db_resource_stats`: the database's CPU as a share of its service tier's limit, the figure the Azure portal shows as *CPU percentage* (before 0.36.0 it read 0 % there). Blocking is the longest `wait_time` of a blocked request in `sys.dm_exec_requests`. Page life expectancy and memory grants pending are Buffer Manager and Memory Manager counters. Log space is the highest `Percent Log Used` across databases, with that log's `Log File(s) Size (KB)` and its `log_reuse_wait_desc` from `sys.databases`; from 0.75.0 a log under 1 GB that can be reused sorts after the others, so the one checked is the fullest that counts. Page life expectancy's threshold is scaled by the Buffer Manager's `Database pages` (8 KB each) from 0.75.0. TempDB is `Free Space in tempdb` against `Data File(s) Size`. All come from `sys.dm_os_performance_counters`.
- **No extra query:** blocking, log and TempDB are read by the same live metrics query that feeds the charts. Their figures aren't saved to history, but [alerts](Alerts.md) on them are.
- **Missing counters:** an indicator the server doesn't report (some counters are missing on certain Azure SQL tiers) is `HealthLevel.Unknown`: it doesn't colour the dot, and from 0.75.0 it is left out of the score, which is shared out over the checks that reported. Before 0.75.0 it kept its points.
- **Validation:** values out of range in a hand-edited `settings.dat` fall back to the defaults.
- **Code:** thresholds and grading are in `SqlVitals/Engine/Monitoring/` (`HealthThresholds` with its `HealthWeights` and `PageLifeExpectancyFixed`, `HealthRules`, `LiveMetricSample`). `HealthRules.Grade` grades each indicator separately through `HealthRules.Read`, which also scales page life expectancy (`HealthThresholds.For`) and spares a small reusable log; `Evaluate` (the dot) and the [alert tracker](Alerts.md#under-the-hood) both use it, and Settings' preview grades again through `HealthRules.Regrade`, so it never disagrees. Collectors are run by `Desktop/Services/MonitoringManager.cs`, one `MonitoringSession` per monitored connection. The editor is `Desktop/Controls/HealthThresholdsEditor`.
- **Which connections are monitored:** `MonitoringPlan.Decide` in `Engine/Monitoring/MonitoringPlan.cs` decides, from Start monitoring automatically, the active connection, Monitor in background, the Start and Stop pressed this run (`MonitoringOverrides`), a missing password and the Entra sign-in. It's a pure function, unit tested in `Engine.Tests/Monitoring/MonitoringPlanTests.cs`. `MonitoringManager.Sync` only starts and stops sessions to match it. The upgrade rule is `MonitoringPlan.StartAutomatically`: `StartMonitoringAutomatically` is nullable in `settings.dat`, so a file without it but with connections reads as on. `Desktop.Tests/MonitoringManagerTests.cs` proves, with a repository that counts calls, that with the setting off neither a start nor a connection switch sends a query.
- **Score:** `SqlVitals/Engine/Monitoring/HealthScore.cs`. `HealthScore.From(readings, time, weights)` turns the readings of `HealthRules.Grade` into the score and its checks: each check's `Worth` is its weight's share of 100 among the checks that reported, as whole points that add up to 100, and `Verdict` is the worst state and its reason (from 0.75.0); `HealthScore.Unavailable` is the 0 for a failed collection. `MonitoringSession.Score` holds the latest one. The panel is `Desktop/Controls/HealthScoreBreakdown`, shown in a popup by the sidebar (`MainWindow`) and by Live charts. The Overview's *What is costing points* is `Engine/Home/ScoreCosts` over the same checks (tested against the weights for every check state in `Engine.Tests/Home/OverviewTests.cs`). Unit tested in `Engine.Tests/Monitoring/HealthScoreTests.cs`. From 0.53.0 scores are saved to the history (`health_scores`).
- **Backoff:** unreachable servers retry with exponential backoff, capped at 5 minutes.
- **A sample held up by a lock (from 0.69.0):** monitoring waits at most 2 s for a lock (a schema change on what it reads), then skips that sample. The dot and score keep the last grade, and the score's text says the sample was skipped; it doesn't count as a failure or towards an alert. See [Server Impact → Locks](Server-Impact-and-Permissions.md#locks-what-can-still-wait).
- **Stopping cancels:** **■ Stop**, or removing the connection, cancels the sample being read, on the server too.
