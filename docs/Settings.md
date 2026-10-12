# Settings

**⚙️ Settings** holds everything you can configure. Changes apply straight away; no restart is needed.

> **Where:** sidebar → **Settings**, at the foot (opens automatically on first launch, from 0.78.0 on the [first-run steps](Getting-Started.md#1-connect-check-and-start)); a server's **⋯ → Edit connection…** on All servers opens its page · **Since:** categories and a page per connection from 0.54.0 ([#131](https://github.com/aselasampath/sqlvitals/issues/131))

Settings has its own side menu, next to the app's sidebar, with a category per kind of setting. It opens on **Connections**, or on the category you last used since SqlVitals started.

| Group | Category | What you set | Saved |
|---|---|---|---|
| Servers | [Connections](#connections) (with how many) | Saved servers: how to reach them, their monitoring and alerts, their own thresholds | With the page's **Save** |
| Monitoring | [Monitoring & history](#monitoring--history) | Start monitoring automatically, command timeout, snapshot interval, retention, how query text is kept, clearing the history or its query text | As you change them |
| Monitoring | [Health thresholds](#health-thresholds) | The default thresholds: when the health dot turns 🟠 or 🔴, and so what lowers the health score | With **Save** |
| Notifications | [Alerts & chat](#alerts--chat) | How long a breach lasts before it's an alert, Windows notifications, Slack and Teams channels | As you change them (a channel with its own **Save channel**) |
| Notifications | [Notification area](#notification-area) | Whether minimizing or closing the window keeps SqlVitals running | As you change it |
| App | [Appearance](#appearance) | Light or dark theme, grid density | As you change it |
| App | [Activity log & diagnostics](#activity-log--diagnostics) | When monitoring started and stopped, every settings change, and the app's log files | — |

Categories for the whole app say *For every connection* under their title. A setting for **one** connection is only ever on that connection's page, so it's always clear which a change affects.

- The side menu works with the keyboard: **Tab** into it, **↑ / ↓** to move, **Space** or **Enter** to open. Screen readers announce the selected category.
- Other pages link straight to a category or a connection's tab: **Change** under a server's Checks table on [Home](Your-Servers.md) opens that connection's **Health thresholds** tab, **Edit connection** its **Connection** tab, and **+ Add a connection** a new connection's page.
- How settings are stored (`settings.dat`) and logged ([activity log](#activity-log)) didn't change in 0.54.0: an existing file loads as it was.

## Connections

![Settings, Connections: a card per saved connection](images/settings/connections.png)

A card per saved connection, by name as in the breadcrumb's server menu, two a row (one in a narrow window). Each card shows:

- the **health dot** and name, and a label: **ACTIVE** (the connection every page queries), **BACKGROUND** (monitored while another one is active) or **OFF** (neither);
- the server / database;
- tags for the sign-in type (*SQL login*, *Entra MFA*, *Windows auth*), the monitoring mode (*Monitored*, *Monitor in background*, *Started by you* or *Not monitored*) and *Default thresholds* or *Own thresholds*;
- its state now: *Score 70 · Critical*, *Unreachable · can't connect*, *Waiting for the first sample* or *No score: not monitored*, coloured like the dot;
- **Check permissions** (from 0.78.0), which signs in and lists the permissions the login lacks, the pages each affects, and the GRANT script that adds them (see [Check permissions](Connections.md#check-permissions)); **Test**, which signs in with the saved details and shows the result on the card for half a minute; and **Edit**, which opens the connection's page.

The active connection has the accent border. **+ New connection** at the top and the **+ Add a connection** card at the end open an empty connection page. With no connections, the category says so and offers **+ New connection**; from 0.78.0, when SqlVitals starts with none, Settings opens on the [first-run steps](Getting-Started.md#1-connect-check-and-start) instead, until you skip them. Every field is explained in [Connections](Connections.md).

### A connection's page

![A connection's page, Connection tab](images/settings/connection-page.png)

**← Connections** goes back to the cards. The header shows the dot, name, status and score (*CRITICAL · 70*), **ACTIVE** when it is, the server / database / sign-in, and **View on Home** for its [Overview](Your-Servers.md#a-servers-overview). **Test connection** signs in with what's typed, saved or not. **⋯ → Remove connection…** removes it after a confirmation.

| Tab | What's on it |
|---|---|
| **Connection** | *Server*: server name, authentication, login, password, remember password. *Database & security*: database (open the list to browse the server), encrypt, trust server certificate. *Name*: the connection name. |
| **Monitoring & alerts** | **Monitor in background**; **Windows notifications** for this connection (off mutes it); and which [chat channels](#chat-channels) post its alerts, read only, with a link to Alerts & chat. |
| **Health thresholds** | [Its own thresholds](#a-connections-own-thresholds). The tab shows an **OWN** tag when the connection uses them. |
| **Advanced** | Connection timeout, and additional connection-string parameters. |

A bar along the bottom saves **every tab at once**: **Discard**, **Save**, and **Save & make active** (save, then switch every page to it and open its Overview; not shown for the active connection). Moving between tabs keeps what you've typed, and the bar says where: *● Unsaved changes on Connection and Health thresholds*. Typing a value and putting it back leaves nothing unsaved.

- **Saving checks the server first** when anything about how SqlVitals connects changed (server, authentication, login, password, database, encryption, timeout, parameters), and saves nothing if it can't sign in, so the list only ever holds working connections. A new name, the monitoring and alert settings, or thresholds save **without** contacting the server, so a server that's down can still be renamed, muted or given its own thresholds.
- A connection's settings, its own thresholds and its Windows notifications are saved in **one** write, logged as one change per field in the [activity log](#activity-log).
- **Leaving with unsaved changes asks** first, whether you go back to the cards, open another category or another page in the sidebar: **Yes** saves (and stays if the save fails), **No** discards, **Cancel** stays.
- A new connection uses the same page, titled *New connection*. The first connection saved becomes the active one.
- Field help text and the required (*) markers are as before.

### A connection's own thresholds

![A connection's Health thresholds tab: its own thresholds and weights, with the preview](images/settings/connection-thresholds.png)

**Use its own health thresholds** is a switch. Off, the table shows the [default thresholds](#health-thresholds) read-only, with a link to them; on, it starts from them and you can edit them for this connection alone, e.g. for a server that always runs hot.

The table is the same Checks table as the score's breakdown (click a server's score): **Check, Now, Warning, Critical, Weight, Points**. Under each check's name is the default, e.g. *Default: ≥ 75 % / ≥ 90 % · weight 16*; a box that differs from it has an accent border. Leave a threshold blank to turn that level off. From 0.75.0 the **Weight** is a box too, and page life expectancy has **Per 4 GB of buffer pool** under its name (see [Health thresholds](#health-thresholds)). **Reset to defaults** puts the defaults back in the boxes.

While you type, **Now** and **Points** grade the connection's latest sample on what's in the boxes, and the header says what it would score: *On the latest sample, UAT would score 76 → 84*. It's graded by the same rules as the health dot, so saving shows the same. A check the server didn't report shows *No data* and a dash for its points: it isn't scored. With no sample (the connection isn't monitored, or can't be reached) there's no preview.

## Monitoring & history

![Settings, Monitoring & history](images/settings/monitoring-and-history.png)

| Setting | Default | Effect |
|---|---|---|
| **Start monitoring automatically** | **Off** for a new installation; **on** for one upgraded from before 0.46.0 | *"Collect live metrics, history and alerts as soon as SqlVitals starts or connects. Off: nothing is collected until you press Start."* On, the active connection and those with **Monitor in background** (on each connection's Monitoring & alerts tab) are monitored from the start. Applied straight away: on starts them, off stops every connection (their active alerts end). |
| **Command timeout** | 30 s | The longest any single diagnostic query may run before it's cancelled, on **every** connection, monitoring included. Raise it if screens such as Index Health or Implicit Conversions time out on a very large server. Saved when you leave the box or press **Enter**; before 0.54.0 it was saved with a connection. It overrides `QuerySettings:CommandTimeoutSeconds` in `appsettings.json`. |
| **Snapshot every** | 5 minutes (1, 5, 15, 30) | How often waits per type, file I/O, memory, top queries and Perfmon counters are saved. Shorter = more detail, more load on the server and more disk. Live Metrics samples are saved at their own interval regardless. |
| **Keep history for** | 14 days (7, 14, 30, 90) | Older history is deleted automatically. **Shortening** it asks for confirmation, then deletes straight away. Use at least 14 days for week-over-week baselines. |
| **Store query text in history** (from 0.77.0) | **Without literals** for a new installation; **Full** for one upgraded from before 0.77.0, which is asked once | How the busiest queries' text is saved with their numbers. **Without literals**: every value replaced with `?` (`WHERE Email = ?`), so names, emails or passwords in a statement aren't saved. **Full**: as SQL Server ran it. **Off**: no text, only each query's hash; CPU, reads and executions are saved either way. Applies to text read from then on, here and in the [collector service](Collector-Service.md) when it watches one of your servers; text already saved stays until **Clear stored query text…**. See [Security and Privacy](Security-and-Privacy.md#query-text-in-the-history). |

Either way you can start and stop any connection with **▶ / ■** in the sidebar's server header, on All servers, on a server's Overview or **💓 Live charts**, or from the notification-area icon's menu, until SqlVitals exits. See [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring). **Alerts are only raised for connections being monitored.** The category also shows the **history file's size** on disk, updated while it's open.

**Clear history…**, in its own card, deletes all history, alerts included, for **every** connection, after a confirmation. Monitoring carries on, and new history is saved from the next sample. This can't be undone. The [activity log](#activity-log) is **kept**, and records that history was cleared, when and by whom.

**Clear stored query text…** (from 0.77.0), in the same card, deletes only the text of every saved query, for every connection, after a confirmation; the numbers stay, and the freed space in the file is overwritten. Text is saved again as **Store query text in history** says. When the collector service is installed on this PC it is asked to clear its own history's text too. The activity log records it as **Query text cleared**.

See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md) and the technical [Monitoring History Database](Monitoring-History-Database.md).

## Health thresholds

![Settings, Health thresholds: a threshold and a weight per check, page life expectancy per 4 GB of buffer pool, and the preview](images/settings/health-thresholds.png)

The thresholds every connection without [its own](#a-connections-own-thresholds) is graded on, in the same table: a **Warning** and **Critical** box per check, and its **Weight**. Leave a threshold blank to turn that level off; a box that differs from SqlVitals' own defaults has an accent border, and **Reset to defaults** puts those back.

From 0.75.0 (#173):

- **Weight** (0 to 100) is how much the check counts in the [health score](Health-Dot-and-Background-Monitoring.md#change-the-weights). Weights are relative: the score is always out of 100, shared out over the checks that reported. **0** leaves a check out of the score while its thresholds still colour the dot and raise alerts. At least one weight must be above 0. From 0.76.0, with disk, backups and jobs, the defaults are CPU 16, Blocking 16, Log 14, Disk 14, PLE 10, Backups 10, Grants 8, TempDB 8 and Jobs 4, adding up to 100 (in 0.75.0 the six were 20, 20, 16, 12, 20 and 12).
- **and free space below**, under Disk free (from 0.76.0), holds its GB thresholds: a volume reaches a level only when it is below both the % and the GB, so a large volume isn't flagged for a small share. Blank a GB box to judge that level on the % alone.
- **Per 4 GB of buffer pool**, under page life expectancy, is on by default: its boxes are seconds per 4 GB, so 300 means 4,800 s on a server with 64 GB of buffer pool and 150 s on one with 2 GB. Turn it off for a fixed number of seconds.
- Under the table, every monitored connection on these thresholds says what it would score on what's typed: *On the latest sample, UAT would score 76 → 84*.
- Each change is logged in the [activity log](#activity-log): *Health weight: CPU* 20 → 0, *Health threshold: Page life expectancy scaling* Per 4 GB of buffer pool → Fixed. A connection's own are *Own health weight: …*.

This category keeps an explicit **Save** bar, so a half-typed threshold never grades a sample: **Save** checks every box and applies them to every connection without its own, straight away; **Discard** puts back what's saved. Leaving with unsaved thresholds asks first. See [Health Dot and Background Monitoring](Health-Dot-and-Background-Monitoring.md).

## Alerts & chat

![Settings, Alerts & chat](images/settings/alerts-and-chat.png)

| Setting | Choices | Default | Effect |
|---|---|---|---|
| **Raise an alert after** | 1, 2, 3, 5, 10 samples | 3 | Samples in a row past a health threshold before an [alert](Alerts.md) starts. The same number back to normal ends it, so a value hovering around a threshold raises one alert, not many. At the default 10-second Live Metrics interval, 3 samples is 30 seconds. Applied from the next sample. |
| **Windows notifications** | A switch | On | On while any connection raises a [Windows notification](Notifications-and-Tray.md#windows-notifications) when one of its alerts starts or turns critical. **Off mutes every connection; on unmutes them all.** The muted connections are listed under it, each a link to its **Monitoring & alerts** tab, where you mute or unmute one connection. Muted alerts are still recorded, listed and posted to chat channels. Also in the notification-area icon's menu (**Notifications**). |

What raises an alert is set by the [health thresholds](#health-thresholds).

### Chat channels

From 0.52.0, the Slack and Microsoft Teams channels alerts are posted to, as cards two a row. Above them, a warning says plainly that alerts stop when this PC sleeps or SqlVitals closes, and that each channel is told when SqlVitals [stops and starts watching](Chat-Notifications.md#stopped-watching-and-watching-again) (0.70.0). Each card shows:

- the name, the service and the webhook URL masked (`hooks.slack.com/…/x7Qa`);
- tags for its connections (*All monitored connections*, or their names) and severity (*Warnings and critical* or *Critical only*), *No end messages* when it doesn't post ends, and its heartbeat (*Heartbeat daily 09:00*, *+ ping* with a ping URL);
- a label: **WORKING** (a message got through), **FAILING** (messages aren't getting through, [#122](Chat-Notifications.md#-failing)), **NOT USED YET** or **OFF** (paused), and under it *Last posted 10:42*, *Never used* or *⚠ Failing since 10:42: webhook not found (404)*;
- **Send test**, which posts a test message to the channel as saved and shows the result under the cards, and **Edit**.

**+ Add channel** and **Edit** open the channel form: **Name**, **Webhook URL** (masked once saved; **Show** reveals it), **Service** (picked from the URL), **Connections** (all monitored connections, or only the ones you tick), **Post** (warnings and critical, or critical only), **Also post when an alert ends** (on by default: *Back to normal* for the alerts the channel was told about; *stopped watching* and *watching again* are posted either way), **Heartbeat** (off, every 1–12 hours, or daily at a time; with **How to get an alert when a heartbeat is missing** and **Copy steps**), **Heartbeat ping URL** (optional, masked once saved; see [Heartbeat](Chat-Notifications.md#heartbeat)) and **On** (off pauses the channel without removing it). **Save channel** checks the URL first and applies straight away; **Remove channel…** removes it after a confirmation.

The webhook URLs are stored encrypted and never logged. Setup steps for each service, what each test result means and the URL checks are on [Chat Notifications](Chat-Notifications.md).

## Collector service

Optional, from 0.74.0 (#172). This is for the [collector service](Collector-Service.md), a Windows service that keeps watching servers when SqlVitals is closed. Without it, this page says how to get it and nothing else changes.

| Setting | Default | Effect |
|---|---|---|
| **The service on this PC** | — | Its state (*Not installed*, *Running*, *Stopped*), and how many of your servers it watches; **Start** and **Stop**. |
| **When monitoring starts** | Ask each time | What ▶ Start does while the service is installed: ask, watch the server in SqlVitals, or hand it to the service. *Don't ask again* in the question sets it too. |
| **Your servers** | — | Each server, who watches it, and **Hand over** or **Take back**. A server the service can't sign in to (Entra MFA, a SQL login without a saved password) can't be handed over; the button's tooltip says why. |
| **A collector on another PC** | None | The folder (or file) of another PC's collector [share copy](Collector-Service.md#a-collector-on-another-pc); its servers that match yours are shown from it and aren't collected here. |

## Notification area

| Setting | Default | Effect |
|---|---|---|
| **Keep SqlVitals running in the notification area** | On | Minimizing or closing hides the window to the notification area, and monitoring, history, alerts and notifications carry on. Click the icon to bring it back; right-click it → **Exit** to quit. Off: minimizing goes to the taskbar and closing the window exits. |

See [Notifications and Tray](Notifications-and-Tray.md).

## Appearance

**Theme**: switch between **Light** (default) and **Dark**. The choice isn't remembered yet, so the app starts in the light theme every time.

**Grid density** (from 0.64.0): **Comfortable** (the default, the look grids have always had) or **Compact**, which keeps the same 12 px text with less room around it, so rows are about 19 px instead of 27 and a screen shows about 40 % more of them. It applies to every grid at once, on every screen, in both themes, with no restart, and is remembered (and logged in the [activity log](#activity-log)). See [Working with Grids](Working-with-Grids.md#grid-density).

## Activity log & diagnostics

### Activity log

A record of what was done, kept in the history file (`history.db`, table `activity_log`) from 0.46.0. It explains a gap in the history, or an alert that never came, and who changed what, when. Newest first, with filters for **connection**, **event** and **period** (24 hours to 365 days), **↻ Refresh**, and **📤 Export CSV** (the rows shown, in their order; right-click the grid for Copy too).

| Event | Logged when |
|---|---|
| **Monitoring started** / **Monitoring stopped** | For each connection, with the source: you (on Live Metrics, on a server's Overview from 0.59.0, in the sidebar or on All servers, in the connection selector before 0.57.0, in the quick switcher from 0.60.0, in the tray menu, or in Settings), automatically at startup, because the setting changed, because the connection was edited or removed, or because the app exited |
| **App started** / **App exited** | Each run. A run that was killed, crashed or lost power has no *App exited*; the next start says *The previous run didn't exit cleanly* |
| **Setting changed** | Every setting on this page (and the ones saved on other pages: Backups RPO, File I/O, Processes, Query Store charts, Query Regressions), with the old and new value. The theme too. Not which sidebar groups you collapse or expand. |
| **Connection added** / **edited** / **removed** | Edited: one row per field that changed, with its old and new value |
| **History cleared** | **🗑 Clear history** |
| **Query text cleared** | **Clear stored query text…** (from 0.77.0); the collector service's own row says which request it cleared for |
| **Chat channel added** / **edited** / **removed** | From 0.52.0. The channel's name is in *Detail* (added, removed) or *Setting* (edited, e.g. *DBA team: On*). A new webhook URL only shows as *changed*. |

Each row also has the **Windows user** and **PC name**, so a shared or copied history file still says who did it.

- **Never logged:** passwords, connection strings (the connection's *Additional parameters* too), access tokens, webhook URLs or any other secret. For those the row only says *changed*. From 0.77.0 every value and detail is also masked as it's written, in case an error message in it echoes one back.
- A value that didn't change isn't logged: saving without changing anything writes nothing.
- Logging never holds up the app or monitoring: rows are saved in the background with the rest of the history. If the file can't be written, the row goes to the [app log](#diagnostics) instead.
- **Kept for 365 days**, whatever **Keep history for** says; older rows are deleted with the rest of the old history. A damaged history file is renamed and a new one started, and the log starts again with it.
- It's a record, not a tamper-proof audit trail: it's a local file your Windows account can edit.

The table and example queries are in [Monitoring History Database](Monitoring-History-Database.md#activity_log).

### Diagnostics

SqlVitals writes errors and warnings to a daily log file. **Open log folder** opens it in Explorer. Attach the latest file when you [report a bug](https://github.com/aselasampath/sqlvitals/issues).

- Location: `%AppData%\SqlVitals\logs\SqlVitals-yyyyMMdd.log`, one file per day.
- Files older than **14 days** are deleted automatically.
- **Passwords and connection strings are removed** before anything is written.

---

### Under the hood: where settings live

| What | Where | Notes |
|---|---|---|
| Connections, chat channels (with their webhook URLs), command timeout (since 0.54.0 saved on its own), Start monitoring automatically, thresholds, alert samples, muted connections, notification-area choice, history settings, regression criteria, the Backups page's RPO ages, the File I/O page's latency thresholds and reading interval, the Processes page's auto-refresh interval and Start/Stop, its Map or Grid view and Group by login, the page last open, the sidebar's state, when each server's Overview was last opened (from 0.59.0) | `%AppData%\SqlVitals\settings.dat` | Per Windows user, DPAPI-encrypted. Roams with your profile. |
| Monitoring history and the activity log | `%LocalAppData%\SqlVitals\history.db` | Local, not roaming. See [Monitoring History Database](Monitoring-History-Database.md). |
| Logs | `%AppData%\SqlVitals\logs\` | 14-day retention (`Services/FileLog.cs`) |
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
