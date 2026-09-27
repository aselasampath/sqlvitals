# Getting Started

This page takes you from a fresh install to your first look at a server in about five minutes.

## 1. Add your first connection

On first launch SqlVitals opens **⚙️ Settings**.

1. **Connection name**: something you'll recognise, like *Production* or *UAT*. Leave it blank to use *server / database*.
2. **Server name**: for example `myserver.database.windows.net`, `localhost\SQLEXPRESS`, or `host,1433` for a custom port.
3. **Authentication**: *Windows Authentication*, *SQL Server Authentication* (login and password) or *Microsoft Entra MFA* (sign in with your Microsoft account).
4. **Database name**: leave it blank for your login's default database, or open the list to browse the server.
5. Click **⚡ Save & Connect**.

SqlVitals checks the connection before saving it. If the check fails, it tells you why and saves nothing. More options, such as encryption, certificates and timeouts, are explained in [Connections](Connections.md).

## 2. Find your way around

```
┌──────────────────────┬──────────────────────────────────────────────┐
│ SqlVitals            │                                              │
│ ACTIVE CONNECTION    │                                              │
│ 🟢 Production     ▾  │        The screen you picked appears here    │
│──────────────────────│                                              │
│ 💓 Live Metrics      │                                              │
│ 📊 Top Waits         │                                              │
│ ⚡ Active Waits      │                                              │
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
| **Sidebar buttons** | One per screen. The screens are grouped by topic in the [feature map](Home.md#feature-map). |
| **↻ Refresh** | Reloads the current screen. Most screens load once when you open them and again when you press Refresh. |
| **Status bar** | Shows what's loading, and errors. If a screen's automatic refresh fails, the error appears here instead of a pop-up. |

## 3. Take a first look

A good first tour, in order:

1. **💓 Live Metrics**: the live dashboard. Press **Start** if it isn't already collecting. Watch CPU, waits and throughput for a minute.
2. **📊 Top Waits**: what SQL Server has spent most of its time waiting on since it last started. This is the single best clue to *why* a server is slow. See [Wait Statistics](Wait-Statistics.md).
3. **🔥 Resource Queries**: the queries using the most CPU and I/O.
4. **🔍 Index Health**: missing and unused indexes.

## 4. Add your other servers

Repeat step 1 for each server you look after. Each connection with **Monitor in background** ticked (the default) keeps collecting while you look at another one. That keeps its health dot and score current and fills its history. See [Health Dot and Background Monitoring](Health-Dot-and-Background-Monitoring.md).

## Tips

- **Right-click any grid** to copy rows or export them to CSV. See [Working with Grids](Working-with-Grids.md).
- **Hover** column headers, buttons and dots. Most have a tooltip explaining them.
- Prefer a dark screen? **Settings → Appearance → Theme**.
- Something not working? See [Troubleshooting and FAQ](Troubleshooting-and-FAQ.md). **Settings → Diagnostics → Open log folder** shows the app's log.

## Next

- [How-To Recipes](How-To-Recipes.md): step-by-step guides for common investigations.
- [Live Monitoring](Live-Monitoring.md): the live screens in detail.
