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
┌──────────────────────┬──────────────────────────────────────────────┐
│ SqlVitals            │                                              │
│ ACTIVE CONNECTION    │                                              │
│ 🟢 Production     ▾  │        The screen you picked appears here    │
│──────────────────────│                                              │
│ 🏠 Home              │                                              │
│ OVERVIEW           ⌄ │                                              │
│ 💓 Live Metrics      │                                              │
│ 🔔 Alerts            │                                              │
│ ACTIVITY NOW       ⌄ │                                              │
│ 🔗 Processes         │                                              │
│ …                    │                                              │
│ ⚙️ Settings          │                                              │
│ ↻ Refresh            │                                              │
├──────────────────────┴──────────────────────────────────────────────┤
│ Ready                                   ← status bar: progress and errors │
└─────────────────────────────────────────────────────────────────────┘
```

| Part | What it does |
|---|---|
| **Active connection** | Which server every screen is looking at. Switch here at any time; no restart needed. |
| **Health dot** | 🟢 healthy, 🟠 warning, 🔴 critical or unreachable, ◯ not monitored. Hover it to see why. See [Health Dot](Health-Dot-and-Background-Monitoring.md). |
| **Health score** | The number beside the dot: 100 is healthy, lower needs attention, 0 is unreachable. Click it to see which checks lowered it. See [Health score](Health-Dot-and-Background-Monitoring.md#health-score). |
| **Sidebar groups** | The screens, grouped by the question you're asking (see the table below). Click a group's name, or Tab to it and press Enter or Space, to collapse or expand it; hover it for the question it answers. SqlVitals remembers which groups you collapsed. Opening a screen in a collapsed group, from a notification or a link say, expands it; a collapsed group holding the screen you're on is shown in the accent colour with a **●**. |
| **Sidebar buttons** | One per screen. A dimmed button marked **N/A** is a screen the connected server doesn't have (on Azure SQL Database: TempDB, Agent Jobs, Backups and Security Audit); hover it to see why. **🏠 Home** is above the groups, **📤 Export / AI** and **⚙️ Settings** below them. |
| **🏠 Home** | Every saved connection as a tile with its health score, the ones that need attention first. With two or more connections, SqlVitals opens here. See [Your Servers](Your-Servers.md). |
| **↻ Refresh** | Reloads the current screen. Most screens load once when you open them and again when you press Refresh. |
| **Status bar** | Shows what's loading, and errors. If a screen's automatic refresh fails, the error appears here instead of a pop-up. |

The sidebar groups (from 0.48.0):

| Group | Screens | The question it answers |
|---|---|---|
| **Overview** | 💓 Live Metrics, 🔔 Alerts | Is the server healthy, and has anything gone wrong? |
| **Activity now** | 🔗 Processes, ⚡ Active Waits, 💥 Deadlocks, 🔌 App Connections | Who is connected, what is running, and who is blocking whom? |
| **Waits** | 📊 Top Waits, 📈 Wait Trend | What is the server waiting on, and how has that changed over time? |
| **Queries** | 🔥 Resource Queries, 📋 Query Store, 🐢 Query Regressions, 🔎 Plan Cache Health, ⚠️ Implicit Conv., 🛰️ SP Trace | Which queries cost the most, which got slower, and why? |
| **Resources** | 🧠 Memory Grants, 🗄️ TempDB, 💽 File I/O, 📦 DB Storage, ⏱️ Perfmon | Is memory, TempDB, disk or storage the bottleneck? |
| **Maintenance** | 🔍 Index Health, 📉 Stale Stats, 💾 Backups, 🕒 Agent Jobs | Are indexes, statistics, backups and jobs looked after? |
| **Configuration** | ✅ Config Checks, 🔒 Security Audit | Is the server set up correctly and securely? |

## 3. Take a first look

A good first tour, in order:

1. **💓 Live Metrics**: the live dashboard. It says *Monitoring is off* at first: nothing is collected from a server until you ask. Press **▶ Start monitoring**, then watch CPU, waits and throughput for a minute.
2. **📊 Top Waits**: what SQL Server has spent most of its time waiting on since it last started. This is the single best clue to *why* a server is slow. See [Wait Statistics](Wait-Statistics.md).
3. **🔥 Resource Queries**: the queries using the most CPU and I/O.
4. **🔍 Index Health**: missing and unused indexes.

## 4. Add your other servers

Repeat step 1 for each server you look after. A connection you started keeps collecting while you look at another one, until SqlVitals exits; press **▶** on its row in the sidebar selector to start one without switching to it. That keeps its health dot and score current, fills its history and raises its alerts.

To have SqlVitals monitor your servers every time it starts, turn on **⚙️ Settings → Monitoring & history → Start monitoring automatically**, and **Monitor in background** on each connection's *Monitoring & alerts* tab to watch while another is active. See [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring).

## Tips

- **Right-click any grid** to copy rows or export them to CSV. See [Working with Grids](Working-with-Grids.md).
- **Hover** column headers, buttons and dots. Most have a tooltip explaining them.
- Prefer a dark screen? **Settings → Appearance → Theme**.
- Something not working? See [Troubleshooting and FAQ](Troubleshooting-and-FAQ.md). **Settings → Activity log & diagnostics → Open log folder** shows the app's log.

## Next

- [How-To Recipes](How-To-Recipes.md): step-by-step guides for common investigations.
- [Live Monitoring](Live-Monitoring.md): the live screens in detail.
