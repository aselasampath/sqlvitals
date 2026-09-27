# Alerts

The health dot tells you what's wrong **now**. **Alerts** tell you what went wrong **and when**: every time a monitored server stays past one of its [health thresholds](Health-Dot-and-Background-Monitoring.md#what-is-checked), SqlVitals records an alert with its start time, end time and worst value. You can look back at the timeline after a busy night.

> **Where:** sidebar → 🔔 Alerts, or click an alert's Windows notification · **Configure:** ⚙️ Settings → *Alerts* (and *Health Thresholds*) · **Since:** 0.34.0 (notifications 0.35.0)

## How an alert starts and ends

Every sample a monitored connection collects (every 10 seconds by default) is checked against its thresholds, one indicator at a time.

| | When | Recorded time |
|---|---|---|
| **Starts** | The indicator has been past its **warning** (or critical) threshold for **3 samples in a row** | The **first** of those samples, when the problem actually began |
| **Becomes critical** | A warning alert's indicator has been past its **critical** threshold for 3 samples in a row | It stays critical from then on |
| **Ends** | The indicator has been back within its thresholds for **3 samples in a row** | The **first** of those samples, when the problem actually stopped |

Needing a run of samples **to end** as well as to start is what stops a value hovering around a threshold from raising a new alert every few seconds. A CPU bouncing between 74 % and 76 % either never raises an alert or raises **one** that stays open, never a stream of them.

**Example** (warning at 75 %, 3 samples, 10-second interval):

| Sample | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---|---|---|---|---|---|---|---|---|---|
| CPU % | 70 | **78** | **80** | **77** | 73 | **79** | **81** | 70 | 71 | 69 |
| Alert | | | | starts, dated sample 2 | | | | | | ends, dated sample 8 |

The single 73 at sample 5 doesn't end it, because the samples after it are past the threshold again. The worst value recorded is 81 %.

## The Alerts page

| Column | What it shows |
|---|---|
| **Status** | **● Active**: still past its threshold. **Ended**: finished. |
| **Severity** | 🟠 **Warning** or 🔴 **Critical**: the worst level it held for long enough |
| **Connection**, **Indicator** | Which server, and which of the six health indicators |
| **Started**, **Ended**, **Duration** | On this PC's clock. Duration keeps counting while it's active. |
| **Worst value** | The highest figure seen, or the **lowest** for page life expectancy |
| **Threshold** | The threshold it crossed, e.g. `≥ 90 %` or `< 300 s` |
| **Ended because** | *Back to normal*, or *Monitoring stopped* (see below) |
| **Details** | What the health dot said at the worst value, e.g. *Log 93% full (Sales)* or *Blocking 3.5 min (4 blocked)* |

- **Filters:** **Connection** (all, or one), **Show** (active and past, active only, past only) and **Period** (last 24 hours, 7 days, 30 days, or all the history kept). The [filter box](Working-with-Grids.md) and column sorting work as on every grid.
- Active alerts are listed first, then the rest, newest first.
- The page **updates by itself** when an alert starts, changes or ends, so you can leave it open.
- The sidebar button shows how many alerts are active, e.g. **🔔 Alerts (2)**.
- Alerts of a **deleted connection** stay in the history and show under *All connections* with the name it had.

## Change when alerts fire

**⚙️ Settings → Alerts → Raise an alert after**: **1, 2, 3 (default), 5 or 10 samples** in a row. The same number of normal samples ends an alert. It takes effect from the next sample, with no restart.

- It's counted in **samples**, so the time depends on each connection's Live Metrics interval. At the default 10 seconds, 3 samples is 30 seconds; 10 samples is 100 seconds.
- **1** alerts on every single breach. Use it only with thresholds you're sure about.
- To change **what** raises an alert, change the [health thresholds](Health-Dot-and-Background-Monitoring.md#change-the-thresholds), for every connection or [just one](Health-Dot-and-Background-Monitoring.md#per-connection-thresholds). A level left blank there raises no alerts either.

## When monitoring stops

An alert needs someone watching. When nothing is, active alerts end with **Ended because: Monitoring stopped**, dated at the **last sample taken**:

- you pressed **Stop** on Live Metrics for that connection;
- you edited or removed the connection;
- you exited SqlVitals (**Exit** in its notification-area menu, or closing the window when [running in the notification area](Notifications-and-Tray.md#running-in-the-notification-area) is off). Just closing or minimizing the window, by default, keeps monitoring running;
- SqlVitals was killed or the PC turned off. Those alerts are ended the next time SqlVitals starts, at the last sample it had saved for that connection.

If the problem is still there when monitoring resumes, a **new** alert starts after the usual run of samples.

A **failed collection** (the server can't be reached) counts neither way, so an active alert stays active while the server is unreachable. The health dot shows 🔴 for that.

## Good to know

- **Which connections:** only saved connections that are monitored, meaning the active one and those with **Monitor in background** ticked. If you pick a connection that isn't monitored, the page says why.
- **No extra load on the server:** alerts are worked out from the samples SqlVitals already collects.
- **How long they're kept:** as long as the rest of the [history](Settings.md#monitoring-history), counted from when the alert ended. An active alert is never deleted. **Clear history** deletes alerts too.
- **Notifications:** from 0.35.0 a Windows notification appears when an alert starts or turns critical; clicking it opens this page filtered to that connection. Mute a connection in **Settings → Alerts → Windows notifications**. See [Notifications and Tray](Notifications-and-Tray.md).
- **One SqlVitals at a time:** from 0.35.0, starting SqlVitals again brings the running copy to the front instead of opening a second one. (Before that, a second copy shared the history file and ended the first one's active alerts when it started.)

---

### Under the hood

- **Pure logic:** `SqlVitals/Engine/Alerts/AlertTracker.cs`, one per monitored connection. `HealthRules.Grade(sample, thresholds)` gives one `HealthReading` per indicator; the tracker turns them into `AlertChange`s (*Started*, *Escalated*, *Worsened*, *Ended*). An indicator with no figure (nothing blocked, a counter not reported) counts as normal. Unit tested in `Engine.Tests/Alerts/`.
- **Wiring:** `MonitoringSession` runs the tracker after every successful collection while it's running, and stops it on Pause and Dispose. `MonitoringManager` raises `AlertsChanged` for the page, the sidebar count and the [Windows notification](Notifications-and-Tray.md#under-the-hood), and applies the Settings value (`AlertSamples` in `settings.dat`).
- **Storage:** each change is queued to the history writer as an `AlertRecord` and saved to the `alerts` table by its id (insert or update), so a lost record is corrected by the next one. At startup, `HistoryWriter.EndAlertsLeftOpen()` ends alerts a previous run left active. See [Monitoring History Database](Monitoring-History-Database.md#alerts).
- **Reading:** `HistoryReader.ReadAlerts(connectionId, from, to)`. The page merges in the running sessions' active alerts, so a new alert shows before it has been written.
