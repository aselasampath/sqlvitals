# Your Servers (All servers)

**All servers** (🏠 Home before 0.57.0) shows every saved connection as a tile with its [health score](Health-Dot-and-Background-Monitoring.md#health-score), the servers that need attention first. **Open** a tile to see why a server's score is what it is: its score over time, the checks that lowered it with a link to the page that explains each one, what's blocking it right now, and its alerts.

> **Where:** **All servers**, at the top of the sidebar, or the first part of the breadcrumb · **Since:** 0.53.0

From 0.57.0 SqlVitals opens on the screen you were on when you last closed it. Before that has happened, it opens on All servers when you have **two or more** saved connections, and on Live Metrics with one.

Home and a server's details only use what monitoring has already collected and the local [history](Monitoring-History-Database.md). Opening Home sends **no query to any server**, and it never contacts a server that isn't monitored. See [Load on the server](#load-on-the-server).

---

## The tiles

At the top, *Your servers* says how many connections there are and how many are monitored, with a count of each status that has any: *1 unreachable · 2 critical · 1 warning · 3 healthy*.

Each tile shows:

| | |
|---|---|
| **Name and status** | The health dot, the connection's name, and its status in words: **CRITICAL**, **WARNING**, **HEALTHY**, **UNREACHABLE**, **WAITING** or **OFF**. The active connection also says **ACTIVE** and has an accent border. |
| **Server / database** | The connection's server and database |
| **Score** | 0 to 100, in the dot's colour, with a sparkline of the **last hour**. |
| **Why** | What lowered it, in the words of the score's breakdown: *Lowered by Page life expectancy −8.* |
| **Six bars** | One per check (CPU, Blocking, PLE, Grants, Log, TempDB), each in its own colour. Point at a bar for its figure and thresholds. |
| **Footer** | When the last sample was taken, and whether it's the active connection or monitored in the background |
| **Button** | **Open** its details, **Retry** a server that can't be reached, or **▶ Start monitoring** one that isn't monitored |
| **⋯ menu** | From 0.57.0: **Open**, **Start monitoring** or **Stop monitoring**, **Why this score…** (the score's breakdown, beside the tile) and **Edit connection…**: what the sidebar's connection selector did for each server before |

The last tile, **+ Add a connection**, opens Settings on a new connection.

### Statuses

| Status | When | Score | Button |
|---|---|---|---|
| **Critical**, **Warning**, **Healthy** | Monitored; the dot's colour on the latest sample | The latest score | **Open** |
| **Unreachable** | Monitored, but the last collection failed: the server couldn't be reached, the login failed, a permission is missing… | **0**. The tile says since when, and the error is in the tooltip. The bars are grey. | **Retry**: tries straight away, and opens its details |
| **Waiting** | Monitored, before its first sample | — | **Open** |
| **Off** | Not monitored: *Not monitored, so there's no score yet*, and why (for example, the password isn't saved) | — | **▶ Start monitoring**, as **⋯ → Start monitoring** does. It's greyed out while it can't start, and its tooltip says why. |

### Order

The tiles are sorted by what needs attention most: **unreachable and critical** first, then **warning**, **healthy**, **waiting** and **off**, by name within each. The active connection is sorted like the others: it's marked, not moved.

The tiles update with every sample. A tile whose status changes moves to its new place, but not while the mouse is over the tiles, so one never moves from under a click.

---

## A server's details

**Open** on a tile opens that server's page, its **Overview → Summary**, and with it the server's workspace: its screens in the sidebar, under its name (see [The server workspace](Getting-Started.md#the-server-workspace)). **← Your servers** goes back.

At the top are the server's name, status, server / database, sign-in and whether it's monitored, and three buttons:

- **Make active**: makes this the connection the server's screens query, as opening one of them does. It's hidden when the connection is already active.
- **Pause monitoring** or **Start monitoring**: stops or starts it until SqlVitals exits, as ▶ / ■ in the sidebar's server header does.
- **Edit connection**: opens its page in ⚙️ Settings → Connections.

### Health score

The score, its status, what lowered it, and how it got there: *Was 100 at 21:45 · lowest today 70 at 21:52*.

### Score over time

A chart of the score over the **last hour**, **24 hours** or **7 days**, from the history.

- Each point is the **lowest** score in its slice of time, so a short dip isn't averaged away.
- The line breaks where monitoring was off.
- **Shading** marks when the server couldn't be reached.
- **Dashed lines** mark where alerts started, labelled with the time and the check: *21:52 Block*.

Scores are recorded from 0.53.0, so the chart starts then.

### Checks

The same table as the score's breakdown: each check's figure, warning and critical thresholds, weight and the points it cost. A check that's costing points has a link under its name to the page that shows why:

| Check | Link | Opens |
|---|---|---|
| Blocking | Blocking chain | 🔗 Processes |
| CPU | Top CPU queries | 🔥 Resource Queries |
| Page life expectancy | Memory | 🧠 Memory Grants |
| Memory grants pending | Memory grants | 🧠 Memory Grants |
| Log space used | Log space | 📦 DB Storage |
| TempDB space used | TempDB | 🗄️ TempDB |

Those pages show the active connection, so a link **makes this connection active first**. Under the table, a line says whether the connection uses the default thresholds or its own, with **Change**, which opens the connection's **Health thresholds** tab in Settings.

### Blocking right now

Shown only while the **Blocking** check is past its warning threshold. It names the head blocker of the biggest blocking chain (its session, login, host and program), how many sessions wait behind it and on what, how long the longest has waited, and the head's statement on one line. **See the blocking chain →** opens 🔗 Processes. If there's more than one chain, it says how many more.

### Alerts

This connection's active alerts, then the ones that ended today, with their severity, figure and threshold, when they started (and ended), and the [chat channels](Chat-Notifications.md) set up to take them. **All alerts** opens 🔔 Alerts filtered to this connection.

---

## When a server can't be reached

The details page of an unreachable server replaces the score and checks with:

- **SqlVitals can't reach *server***: since when, how many attempts have failed, and a countdown to the next one. Retries back off to once every five minutes. Below is the error in full, with:
  - **Retry now**, which tries straight away;
  - **Test connection**, which opens a connection with the saved details and closes it again;
  - **Copy error**.
- **Things to check**, for the way the server name is written:
  - the SQL Server service is running, or, for Azure SQL, the server's firewall allows this PC's IP address;
  - this PC can reach the server's port (firewall, VPN);
  - for a named instance without a port, the SQL Server Browser;
  - the login, when the error says the login failed;
  - the server name itself, with **Edit connection**.
- **Before it went down**: the last score taken while the server could be reached, and when.
- **What happened**: the last good sample, when collections started failing, monitoring starting and stopping, alerts starting and ending, and that it's still failing.

The score chart stays below, with the time it couldn't be reached shaded.

---

## Load on the server

| What | Server queries |
|---|---|
| Home and its tiles | **None.** Scores, statuses and sparklines come from the running monitoring and the local history. |
| A server's details | **None**, except **Blocking right now**: one read of the sessions (the same query as 🔗 Processes) per sample, only while the page is open and blocking is past its warning threshold |
| **Retry**, **Retry now** | One Live Metrics collection, as the next scheduled sample would be |
| **Test connection** | Opens and closes a connection |
| **▶ Start monitoring** | Monitoring starts, as ▶ in the sidebar's server header does: see [Server Impact and Permissions](Server-Impact-and-Permissions.md) |

## Good to know

- A tile's score is the **latest** one, like the dot. Use the chart on the details page for how it got there, and [Alerts](Alerts.md) for how long a problem lasted.
- A connection that isn't monitored never has a score on Home, not even an old one: an old score would read as the server's health now.
- The score's colour is the dot's colour, not a banding of the number: see [Health score](Health-Dot-and-Background-Monitoring.md#health-score).
- With many connections the grid scrolls. A list view for dozens of servers may come later.
