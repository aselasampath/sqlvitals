# Settings

**⚙️ Settings** holds everything you can configure. Changes apply straight away; no restart is needed.

> **Where:** sidebar → ⚙️ Settings (opens automatically on first launch)

| Section | What you set | Details |
|---|---|---|
| [Connection Settings](#connection-settings) | Saved servers and how to reach them | [Connections](Connections.md) |
| [Command Timeout](#command-timeout) | How long a query may run | below |
| [Monitoring History](#monitoring-history) | Snapshot interval, retention, clearing | below |
| [Health Thresholds](#health-thresholds) | When the health dot turns 🟠 or 🔴, and so what lowers the health score | [Health Dot](Health-Dot-and-Background-Monitoring.md) |
| [Alerts](#alerts) | How long a breach lasts before it's recorded as an alert; which connections raise Windows notifications | [Alerts](Alerts.md), [Notifications and Tray](Notifications-and-Tray.md) |
| [Notification Area](#notification-area) | Whether minimizing or closing the window keeps SqlVitals running | [Notifications and Tray](Notifications-and-Tray.md) |
| [Appearance](#appearance) | Light or dark theme | below |
| [Diagnostics](#diagnostics) | The app's log files | below |

## Connection Settings

Add, edit, test and remove saved connections. Every field is explained in [Connections](Connections.md).

## Command Timeout

**Command Timeout (seconds)**, default **30**. The longest any single diagnostic query may run before it's cancelled. Increase it if screens such as Index Health or Implicit Conversions time out on a very large server.

It applies to **every** connection, including background monitoring, and is saved when you click **💾 Save** or **⚡ Save & Connect**. It overrides `QuerySettings:CommandTimeoutSeconds` in `appsettings.json`.

## Monitoring History

| Setting | Choices | Default | Effect |
|---|---|---|---|
| **Snapshot every** | 1, 5, 15, 30 minutes | 5 | How often waits per type, file I/O, memory, top queries and Perfmon counters are saved. Shorter = more detail, more load on the server and more disk. Live Metrics samples are saved at their own interval regardless. |
| **Keep history for** | 7, 14, 30, 90 days | 14 | Older history is deleted automatically. **Shortening** it asks for confirmation, then deletes straight away. Use at least 14 days for week-over-week baselines. |

The section also shows the **history file's size** on disk, updated while the page is open.

**🗑 Clear history** deletes all history, alerts included, for **every** connection, after a confirmation. Monitoring carries on, and new history is saved from the next sample. This can't be undone.

See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md) and the technical [Monitoring History Database](Monitoring-History-Database.md).

## Health Thresholds

A **Warning** and **Critical** box for each health indicator. Leave a box blank to turn that level off. **💾 Save thresholds** applies them; **↺ Defaults** puts the defaults back in the boxes. See [Health Dot and Background Monitoring](Health-Dot-and-Background-Monitoring.md).

## Alerts

| Setting | Choices | Default | Effect |
|---|---|---|---|
| **Raise an alert after** | 1, 2, 3, 5, 10 samples | 3 | Samples in a row past a health threshold before an [alert](Alerts.md) starts. The same number back to normal ends it, so a value hovering around a threshold raises one alert, not many. At the default 10-second Live Metrics interval, 3 samples is 30 seconds. Saved as soon as you pick it and applied from the next sample. |
| **Windows notifications** | A tick box per saved connection | Ticked | A ticked connection raises a [Windows notification](Notifications-and-Tray.md#windows-notifications) when one of its alerts starts or turns critical; untick it to mute it. Saved as soon as you click, without testing the connection, so a server that's down can be muted. Muted alerts are still recorded and listed. Also in the notification-area icon's menu (**Notifications**). |

What raises an alert is set by the health thresholds above.

## Notification Area

| Setting | Default | Effect |
|---|---|---|
| **Keep SqlVitals running in the notification area when the window is minimized or closed** | On | Minimizing or closing hides the window to the notification area, and monitoring, history, alerts and notifications carry on. Click the icon to bring it back; right-click it → **Exit** to quit. Off: minimizing goes to the taskbar and closing the window exits. Saved as soon as you click. |

See [Notifications and Tray](Notifications-and-Tray.md).

## Appearance

**Theme**: switch between **Light** (default) and **Dark**. The choice isn't remembered yet, so the app starts in the light theme every time.

## Diagnostics

SqlVitals writes errors and warnings to a daily log file. **Open log folder** opens it in Explorer. Attach the latest file when you [report a bug](https://github.com/aselasampath/sqlvitals/issues).

- Location: `%AppData%\SqlVitals\logs\SqlVitals-yyyyMMdd.log`, one file per day.
- Files older than **14 days** are deleted automatically.
- **Passwords and connection strings are removed** before anything is written.

---

### Under the hood: where settings live

| What | Where | Notes |
|---|---|---|
| Connections, command timeout, thresholds, alert samples, muted connections, notification-area choice, history settings, regression criteria, the Backups page's RPO ages, the File I/O page's latency thresholds and reading interval, the Processes page's auto-refresh interval and Start/Stop | `%AppData%\SqlVitals\settings.dat` | Per Windows user, DPAPI-encrypted. Roams with your profile. |
| Monitoring history | `%LocalAppData%\SqlVitals\history.db` | Local, not roaming. See [Monitoring History Database](Monitoring-History-Database.md). |
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
