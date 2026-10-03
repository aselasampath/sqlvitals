# Getting Started

This page takes you from a fresh install to your first look at a server in about five minutes.

## 1. Add your first connection

On first launch SqlVitals opens **⚙️ Settings → Connections**. Click **+ New connection**, then on the *Connection* tab:

1. **Connection name**: something you'll recognise, like *Production* or *UAT*. Leave it blank to use *server / database*.
2. **Server name**: for example `myserver.database.windows.net`, `localhost\SQLEXPRESS`, or `host,1433` for a custom port.
3. **Authentication**: *Windows Authentication*, *SQL Server Authentication* (login and password) or *Microsoft Entra MFA* (sign in with your Microsoft account).
4. **Database name**: leave it blank for your login's default database, or open the list to browse the server.
5. Click **Save & make active** in the bar at the bottom.

SqlVitals checks the connection before saving it. If the check fails, it tells you why and saves nothing. More options, such as encryption, certificates and timeouts, are explained in [Connections](Connections.md).

## 2. Find your way around

```
┌──────────────────────┬──────────────────────────────────────────────────────────┐
│ SqlVitals            │ ← → All servers › 🟢 Production › Activity › Processes   │
│ All servers          │             ● Live  every 5 s · As of 14:02:36  ⏸        │
│ Alerts             2 │──────────────────────────────────────────────────────────│
│ Export / AI          │        The screen you picked appears here                │
│┌────────────────────┐│                                                          │
││🟢 Production  96  ■││                                                          │
│└────────────────────┘│                                                          │
│ Overview           › │                                                          │
│ Activity now       ⌄ │                                                          │
│     Processes        │                                                          │
│ …                    │                                                          │
│ Settings             │                                                          │
└──────────────────────┴──────────────────────────────────────────────────────────┘
```

From 0.56.0 one **top bar** across the screen says which server, which database and what time you're looking at, and how fresh it is (in the window it is a single row; it's drawn on two lines here):

![The top bar on Deadlocks: Back and Forward, the breadcrumb, the time range picker, and a Live badge with "every 5 s · As of 14:02:30" and Pause](images/top-bar/live.png)

| Part | What it does |
|---|---|
| **The sidebar's global part** | From 0.57.0, at the top: **All servers** (every server at a glance, see [Your Servers](Your-Servers.md)), **Alerts** with the number of active alerts, and **Export / AI**. **Settings** is at the foot. |
| **The server part** | From 0.57.0, under it: the server you've opened, its **health dot**, **health score** and **▶ / ■** to start or stop monitoring it, then its screens in groups. Open a server from its tile on **All servers**, or switch with the breadcrumb's server menu. On All servers, Alerts and Settings it says *Open a server from All servers*. See [The server workspace](#the-server-workspace). |
| **Health dot** | 🟢 healthy, 🟠 warning, 🔴 critical or unreachable, ◯ not monitored. Hover it to see why. See [Health Dot](Health-Dot-and-Background-Monitoring.md). |
| **Health score** | The number beside the dot: 100 is healthy, lower needs attention, 0 is unreachable. Click it, or Tab to it and press Enter, to see which checks lowered it. See [Health score](Health-Dot-and-Background-Monitoring.md#health-score). |
| **Groups** | The server's screens, grouped by the question you're asking (see the table below). One group is open at a time: the one holding the screen you're on. Click another group's name, or Tab to it and press Enter or Space, to open it instead; hover it for the question it answers. A closed group holding the screen you're on is shown in the accent colour with a **●**. |
| **Buttons** | One per screen, with an icon in the global part. A dimmed button marked **N/A** is a screen the server doesn't have (on Azure SQL Database: TempDB, Agent Jobs, Backups and Security Audit); hover it to see why. |
| **← Back / → Forward** | From 0.55.0, at the left of the top bar: back to the screens you came from, and forward again, as in a browser. Also **Alt+←** / **Alt+→** and the mouse's back and forward buttons. Hover a button to see where it goes. Going back to another server's screen switches to that server. |
| **Breadcrumb** | From 0.56.0, where you are: *All servers › Production › Activity now › Processes*. The server's part has its health dot and score. Click a part to go there: **All servers** opens the server tiles, the group its first screen. From 0.57.0 the server's part is a menu of your saved servers: pick one to see the same screen on it. In a narrow window *All servers* and the group are left out first; the server's name always stays. |
| **Live / Snapshot / History** | From 0.56.0, what the screen is showing, as a badge with its time beside it. **Live** updates on its own: how often, and *As of* the last update; **⏸ Pause** stops it moving so you can read it, and **▶ Resume** starts it again. **Snapshot** was read once: *Taken 14:01:10 · 2 min ago*, in amber once it's 15 minutes old. **History** is a time window: the window, what it's compared with, and *Data to* where the data ends. See [below](#live-snapshot-or-history). |
| **Database** | From 0.56.0, on screens that read one database rather than the whole server (Query Store, Query Regressions, Index Health, Stale Stats, DB Storage): the connection's database. Click it to change it in the connection's settings. |
| **↻ Refresh** | At the right of the top bar (or **F5**): reloads the current screen. Most screens load once when you open them and again when you press Refresh. On a live screen it's beside **▶ Resume** while paused. |
| **Messages** | What's loading, and errors, appear in the top bar, after the time (before 0.56.0 they were at the foot of the sidebar). If a screen's automatic refresh fails, the error appears here, in red, instead of a pop-up. |
| **🔍, ✨ and ☰** | Placeholders for things coming next: jumping to a server or page (**Ctrl+K**), asking AI about the screen, and pinning, collapsing or hiding the sidebar (**Ctrl+B**). They are greyed out until then. |

A server's groups (from 0.48.0; from 0.57.0 Alerts is in the global part, and Overview starts with the server's Summary):

| Group | Screens | The question it answers |
|---|---|---|
| **Overview** | Summary, Live Metrics | Is the server healthy, and has anything gone wrong? |
| **Activity now** | Processes, Active Waits, Deadlocks, App Connections | Who is connected, what is running, and who is blocking whom? |
| **Waits** | Top Waits, Wait Trend | What is the server waiting on, and how has that changed over time? |
| **Queries** | Resource Queries, Query Store, Query Regressions, Plan Cache Health, Implicit Conv., SP Trace | Which queries cost the most, which got slower, and why? |
| **Resources** | Memory Grants, TempDB, File I/O, DB Storage, Perfmon | Is memory, TempDB, disk or storage the bottleneck? |
| **Maintenance** | Index Health, Stale Stats, Backups, Agent Jobs | Are indexes, statistics, backups and jobs looked after? |
| **Configuration** | Config Checks, Security Audit | Is the server set up correctly and securely? |

### The server workspace

From 0.57.0 you open a server and work inside it, instead of picking one in a box at the top of the sidebar:

![The sidebar with Production open: All servers, Alerts and Export / AI, the server's header with its health and Start, and its groups with Activity now open](images/sidebar/server.png) ![With no server open, the server part says how to open one](images/sidebar/no-server.png)

- **Open a server** from its tile on **All servers** (**Open**, or **⋯ → Open**). It opens on its **Overview → Summary**: its health score over time, the checks that cost points, what's blocking it and its alerts. A Windows notification for an alert opens it too.
- **Its screens** are in the sidebar under its name. Opening one makes that server the one SqlVitals queries (the *active connection*); a server whose password isn't saved opens Settings to ask for it first.
- **Switch server** with the breadcrumb's server menu (*Production ▾*): the same screen opens on the other server, or its Overview if it hasn't got that screen (Azure SQL Database has no Agent Jobs, for one).
- **Start or stop monitoring** with **▶ / ■** in the server's header, or in its tile's **⋯** menu on All servers, which also has **Why this score…** and **Edit connection…** for every server.
- **Next time**, SqlVitals opens on the screen and server you were on when you closed it (not Settings). Until it has one, it opens on All servers with two or more connections, Live Metrics with one.

### Live, Snapshot or History

Every screen says how its data relates to time, so you never act on old data without knowing:

| Badge | The screen | Examples | Beside it |
|---|---|---|---|
| **Live** | Updates on its own | Live Metrics, Processes, Memory Grants, File I/O, Wait Trend, Perfmon, Home, Alerts | *every 5 s · As of 14:02:36*, and **⏸ Pause** |
| **Paused** | A live screen that isn't updating right now: paused, auto-refresh not started, or the server not monitored | Processes before you press Start | The time of the last update, and **▶ Resume** |
| **Snapshot** | Was read once, when you opened it or last pressed Refresh | Top Waits, Index Health, Backups, Config Checks, Security Audit | *Taken 14:01:10 · 2 min ago*, amber after 15 minutes |
| **History** | Shows a time window | Query Store, Query Regressions, and Live Metrics, Wait Trend and Perfmon on a past range | The window, *compared with* where it compares, and *Data to 11:55* |

![A snapshot taken 15 minutes ago, its age in amber](images/top-bar/snapshot-old.png)

A screen reader reads the badge and its time out in words, *"Snapshot, taken at 13:48:36, 15 minutes ago, more than 15 minutes old"*, not only its colour. Settings and Export / AI have no badge: they aren't a view of a server's data.

## 3. Take a first look

A good first tour, in order:

1. **💓 Live Metrics**: the live dashboard. It says *Monitoring is off* at first: nothing is collected from a server until you ask. Press **▶ Start monitoring**, then watch CPU, waits and throughput for a minute.
2. **📊 Top Waits**: what SQL Server has spent most of its time waiting on since it last started. This is the single best clue to *why* a server is slow. See [Wait Statistics](Wait-Statistics.md).
3. **🔥 Resource Queries**: the queries using the most CPU and I/O.
4. **🔍 Index Health**: missing and unused indexes.

## 4. Add your other servers

Repeat step 1 for each server you look after. A connection you started keeps collecting while you look at another one, until SqlVitals exits; on **All servers**, press **▶ Start monitoring** on its tile, or **⋯ → Start monitoring**, to start one without opening it. That keeps its health dot and score current, fills its history and raises its alerts.

To have SqlVitals monitor your servers every time it starts, turn on **⚙️ Settings → Monitoring & history → Start monitoring automatically**, and **Monitor in background** on each connection's *Monitoring & alerts* tab to watch while another is active. See [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring).

## Tips

- **Right-click any grid** to copy rows or export them to CSV. See [Working with Grids](Working-with-Grids.md).
- **Go back where you were.** From 0.55.0 a screen you return to opens on the tab, sort, filter, selected row and scroll position you left it with, by the sidebar or by **Back**, and with fresh data.
- **Hover** column headers, buttons and dots. Most have a tooltip explaining them.
- Prefer a dark screen? **Settings → Appearance → Theme**.
- Something not working? See [Troubleshooting and FAQ](Troubleshooting-and-FAQ.md). **Settings → Activity log & diagnostics → Open log folder** shows the app's log.

## Next

- [How-To Recipes](How-To-Recipes.md): step-by-step guides for common investigations.
- [Live Monitoring](Live-Monitoring.md): the live screens in detail.
