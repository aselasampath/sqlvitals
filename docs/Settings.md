# Settings

**⚙️ Settings** holds everything you can configure. Changes apply straight away; no restart is needed.

> **Where:** sidebar → ⚙️ Settings (opens automatically on first launch)

| Section | What you set | Details |
|---|---|---|
| [Connection Settings](#connection-settings) | Saved servers and how to reach them | [Connections](Connections.md) |
| [Command Timeout](#command-timeout) | How long a query may run | below |
| [Monitoring](#monitoring) | Whether monitoring starts by itself | [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring) |
| [Monitoring History](#monitoring-history) | Snapshot interval, retention, clearing | below |
| [Health Thresholds](#health-thresholds) | When the health dot turns 🟠 or 🔴, and so what lowers the health score | [Health Dot](Health-Dot-and-Background-Monitoring.md) |
| [Alerts](#alerts) | How long a breach lasts before it's recorded as an alert; which connections raise Windows notifications; Slack and Teams channels | [Alerts](Alerts.md), [Notifications and Tray](Notifications-and-Tray.md), [Chat Notifications](Chat-Notifications.md) |
| [Notification Area](#notification-area) | Whether minimizing or closing the window keeps SqlVitals running | [Notifications and Tray](Notifications-and-Tray.md) |
| [Appearance](#appearance) | Light or dark theme | below |
| [Activity Log](#activity-log) | When monitoring started and stopped, and every settings change | below |
| [Diagnostics](#diagnostics) | The app's log files | below |

## Connection Settings

Add, edit, test and remove saved connections. Every field is explained in [Connections](Connections.md).

## Command Timeout

**Command Timeout (seconds)**, default **30**. The longest any single diagnostic query may run before it's cancelled. Increase it if screens such as Index Health or Implicit Conversions time out on a very large server.

It applies to **every** connection, including monitoring, and is saved when you click **💾 Save** or **⚡ Save & Connect**. It overrides `QuerySettings:CommandTimeoutSeconds` in `appsettings.json`.

## Monitoring

| Setting | Default | Effect |
|---|---|---|
| **Start monitoring automatically** | **Off** for a new installation; **on** for one upgraded from before 0.46.0 | *"Collect live metrics, history and alerts as soon as SqlVitals starts or connects. Off: nothing is collected until you press Start."* On, the active connection and those with **Monitor in background** are monitored from the start. Saved as soon as you click, and applied straight away: on starts them, off stops every connection (their active alerts end). |

Either way you can start and stop any connection with **▶ / ■** in the sidebar selector, on **💓 Live Metrics**, or from the notification-area icon's menu, until SqlVitals exits. See [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring). **Alerts are only raised for connections being monitored.**

## Monitoring History

| Setting | Choices | Default | Effect |
|---|---|---|---|
| **Snapshot every** | 1, 5, 15, 30 minutes | 5 | How often waits per type, file I/O, memory, top queries and Perfmon counters are saved. Shorter = more detail, more load on the server and more disk. Live Metrics samples are saved at their own interval regardless. |
| **Keep history for** | 7, 14, 30, 90 days | 14 | Older history is deleted automatically. **Shortening** it asks for confirmation, then deletes straight away. Use at least 14 days for week-over-week baselines. |

The section also shows the **history file's size** on disk, updated while the page is open.

**🗑 Clear history** deletes all history, alerts included, for **every** connection, after a confirmation. Monitoring carries on, and new history is saved from the next sample. This can't be undone. The [activity log](#activity-log) is **kept**, and records that history was cleared, when and by whom.

See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md) and the technical [Monitoring History Database](Monitoring-History-Database.md).

## Health Thresholds

A **Warning** and **Critical** box for each health indicator. Leave a box blank to turn that level off. **💾 Save thresholds** applies them; **↺ Defaults** puts the defaults back in the boxes. See [Health Dot and Background Monitoring](Health-Dot-and-Background-Monitoring.md).

## Alerts

| Setting | Choices | Default | Effect |
|---|---|---|---|
| **Raise an alert after** | 1, 2, 3, 5, 10 samples | 3 | Samples in a row past a health threshold before an [alert](Alerts.md) starts. The same number back to normal ends it, so a value hovering around a threshold raises one alert, not many. At the default 10-second Live Metrics interval, 3 samples is 30 seconds. Saved as soon as you pick it and applied from the next sample. |
| **Windows notifications** | A tick box per saved connection | Ticked | A ticked connection raises a [Windows notification](Notifications-and-Tray.md#windows-notifications) when one of its alerts starts or turns critical; untick it to mute it. Saved as soon as you click, without testing the connection, so a server that's down can be muted. Muted alerts are still recorded and listed. Also in the notification-area icon's menu (**Notifications**). |

What raises an alert is set by the health thresholds above.

### Chat channels (Slack and Teams)

From 0.49.0, a list of the Slack and Microsoft Teams channels SqlVitals can post to, each with its service, its webhook URL masked (`hooks.slack.com/…/x7Qa`) and **Never used** or **Last posted 10:42**.

- **+ Add channel** opens a form: **Name**, **Webhook URL** (masked once saved; **Show** reveals it), **Service** (picked from the URL) and **On** (off pauses the channel without removing it). **💾 Save channel** checks the URL first and applies straight away.
- Select a channel to edit it; **🗑 Remove** removes it after a confirmation.
- **📨 Send test message** posts a test message to the selected channel, as saved, and shows the result under the list.

The webhook URLs are stored encrypted and never logged. Setup steps for each service, what each test result means and the URL checks are on [Chat Notifications](Chat-Notifications.md).

## Notification Area

| Setting | Default | Effect |
|---|---|---|
| **Keep SqlVitals running in the notification area when the window is minimized or closed** | On | Minimizing or closing hides the window to the notification area, and monitoring, history, alerts and notifications carry on. Click the icon to bring it back; right-click it → **Exit** to quit. Off: minimizing goes to the taskbar and closing the window exits. Saved as soon as you click. |

See [Notifications and Tray](Notifications-and-Tray.md).

## Appearance

**Theme**: switch between **Light** (default) and **Dark**. The choice isn't remembered yet, so the app starts in the light theme every time.

## Activity Log

A record of what was done, kept in the history file (`history.db`, table `activity_log`) from 0.46.0. It explains a gap in the history, or an alert that never came, and who changed what, when. Newest first, with filters for **connection**, **event** and **period** (24 hours to 365 days), **↻ Refresh**, and **📤 Export CSV** (the rows shown, in their order; right-click the grid for Copy too).

| Event | Logged when |
|---|---|
| **Monitoring started** / **Monitoring stopped** | For each connection, with the source: you (on Live Metrics, in the connection selector, in the tray menu, or in Settings), automatically at startup, because the setting changed, because the connection was edited or removed, or because the app exited |
| **App started** / **App exited** | Each run. A run that was killed, crashed or lost power has no *App exited*; the next start says *The previous run didn't exit cleanly* |
| **Setting changed** | Every setting on this page (and the ones saved on other pages: Backups RPO, File I/O, Processes, Query Store charts, Query Regressions), with the old and new value. The theme too. Not which sidebar groups you collapse or expand. |
| **Connection added** / **edited** / **removed** | Edited: one row per field that changed, with its old and new value |
| **History cleared** | **🗑 Clear history** |
| **Chat channel added** / **edited** / **removed** | From 0.49.0. The channel's name is in *Detail* (added, removed) or *Setting* (edited, e.g. *DBA team: On*). A new webhook URL only shows as *changed*. |

Each row also has the **Windows user** and **PC name**, so a shared or copied history file still says who did it.

- **Never logged:** passwords, connection strings (the connection's *Additional parameters* too), access tokens, webhook URLs or any other secret. For those the row only says *changed*.
- A value that didn't change isn't logged: saving without changing anything writes nothing.
- Logging never holds up the app or monitoring: rows are saved in the background with the rest of the history. If the file can't be written, the row goes to the [app log](#diagnostics) instead.
- **Kept for 365 days**, whatever **Keep history for** says; older rows are deleted with the rest of the old history. A damaged history file is renamed and a new one started, and the log starts again with it.
- It's a record, not a tamper-proof audit trail: it's a local file your Windows account can edit.

The table and example queries are in [Monitoring History Database](Monitoring-History-Database.md#activity_log).

## Diagnostics

SqlVitals writes errors and warnings to a daily log file. **Open log folder** opens it in Explorer. Attach the latest file when you [report a bug](https://github.com/aselasampath/sqlvitals/issues).

- Location: `%AppData%\SqlVitals\logs\SqlVitals-yyyyMMdd.log`, one file per day.
- Files older than **14 days** are deleted automatically.
- **Passwords and connection strings are removed** before anything is written.

---

### Under the hood: where settings live

| What | Where | Notes |
|---|---|---|
| Connections, chat channels (with their webhook URLs), command timeout, Start monitoring automatically, thresholds, alert samples, muted connections, notification-area choice, history settings, regression criteria, the Backups page's RPO ages, the File I/O page's latency thresholds and reading interval, the Processes page's auto-refresh interval and Start/Stop, its Map or Grid view and Group by login | `%AppData%\SqlVitals\settings.dat` | Per Windows user, DPAPI-encrypted. Roams with your profile. |
| Monitoring history and the activity log | `%LocalAppData%\SqlVitals\history.db` | Local, not roaming. See [Monitoring History Database](Monitoring-History-Database.md). |
| Logs | `%AppData%\SqlVitals\logs\` | 14-day retention (`Services/AppLog.cs`) |
| Defaults | `appsettings.json` in the install folder | `QuerySettings:CommandTimeoutSeconds` (30), `QuerySettings:UseNoLock` (true), fallback `ConnectionStrings:SqlServer`, `TraceSettings:SessionName` |

**`appsettings.json`** is for advanced use:

```json
{
  "ConnectionStrings": { "SqlServer": "" },
  "QuerySettings": {
    "CommandTimeoutSeconds": 30,
    "UseNoLock": true
  }
}
```

- `UseNoLock: true` runs every diagnostic query under `READ UNCOMMITTED`, so monitoring never waits behind the locks it's trying to report. Setting it to `false` is rarely a good idea.
- An upgrade keeps your edited copy and writes the new default as `appsettings.json.new`.
