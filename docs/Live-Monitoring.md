# Live Monitoring

Three screens show what the server is doing **right now**:

| Screen | Answers |
|---|---|
| [💓 Live Metrics](#live-metrics) | How busy is the server, and on what? |
| [🔗 Processes](#processes-blocking-chains) | Who is blocking whom, and who is at the head? |
| [⚡ Active Waits](#active-waits) | Which sessions are waiting right now, and on what? |

---

## Live Metrics

> **Where:** sidebar → 💓 Live Metrics (the start page) · **Use it when:** you want a live picture of the server's load · **Needs:** `VIEW SERVER STATE`

A dashboard of live charts, updated every 10 seconds by default.

### How to use it

1. Open **💓 Live Metrics**. If the connection is already monitored, the charts show the last ~10 minutes.
2. If it says **Monitoring is off**, press **▶ Start monitoring** (in the banner or the toolbar). The first sample is taken straight away. **■ Stop monitoring** stops it again. Both last until SqlVitals exits; see [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring).
3. **Sample every**: how often the connection is sampled, 5 s to 2 min. It applies in the background too, and is kept while monitoring is stopped.
4. **Charts**: tick or untick the charts you want to see.
5. **Range picker**: switch from **Live** to **1h**, **24h**, **7d** or **Custom…** to look back in time. **Compare to baseline** adds last week's line. See [History, Time Ranges and Baselines](History-Time-Ranges-and-Baselines.md).

### When monitoring is off

From 0.46.0 SqlVitals doesn't monitor anything until you ask, unless **⚙️ Settings → Start monitoring automatically** is on. Live Metrics still opens first, with a banner: *"Monitoring is off. SqlVitals isn't collecting anything from this server."*, a **▶ Start monitoring** button, and a link to the setting.

- **Nothing is sent to the server** for this page: no Live Metrics query, no history snapshot, and no **Process Map** polling. The sidebar **Refresh** does nothing here either.
- The health score shows **—**: an old score would read as the server's health now.
- **Past ranges** (1h, 24h, 7d, Custom…) still show the history saved earlier.
- If you stopped it, the banner says when (*Stopped by you at 10:42*). If the connection can't be monitored yet (no saved password, Entra sign-in not done), **Start** is greyed out and says why.

### The charts

| Chart | Shows | What to look for |
|---|---|---|
| **Waits – Stack Area** | Wait time per second, stacked by category: CPU, I/O, Locks, Memory, Network, Other | Which colour dominates. A tall *Locks* band means blocking; tall *I/O* means slow storage or big scans. See [Wait Statistics](Wait-Statistics.md#wait-categories). |
| **CPU Usage** | SQL Server's CPU % | Sustained high CPU. Check [Resource Queries](Query-Performance.md#resource-queries) for the cause. |
| **SQL Throughput** | Batch requests, compilations and transactions per second | Sudden drops (a stall) or spikes. Compilations close to batch requests suggests queries aren't being reused. |
| **Physical R/W** | Physical reads and writes per second | Read spikes mean data isn't in memory. |
| **Memory** | Page life expectancy, memory grants pending, buffer cache hit ratio | PLE falling steeply; memory grants pending above 0. |
| **Process Map** | A small version of the blocking chains, refreshed with each sample while monitoring runs | Orange head blockers, listed above the map. Open [Processes](#processes-blocking-chains) for the full view. |

---

## Processes (blocking chains)

> **Where:** sidebar → 🔗 Processes · **Use it when:** users say "it's hanging" or the health dot shows blocking

Live **blocking chains drawn as trees**. The head blocker is at the root and every session hangs under the one blocking it (`blocking_session_id`). Sessions not in a chain are shown below the chains.

### How to use it

1. Open **🔗 Processes**. Pick an **Auto-refresh** interval (5 s to 2 min) and press **Start**. The interval and Start/Stop are remembered, so next time the page starts refreshing by itself.
2. **Look at the line above the map.** It says how many chains there are and lists each **head blocker**, largest chain first: `SPID 55 · 4 blocked · longest wait 12 s`. **Click one** to scroll to it and open its details. *No blocking* means nothing is blocked.
3. Read the tree. The head blocker is orange and labelled **HEAD · n** (n = sessions waiting behind it). **Show Legend** displays the key:

   | Colour key | Meaning |
   |---|---|
   | **Head Blocker** | At the root of a chain. It holds locks others are waiting for and waits on nothing itself. **Start here.** |
   | **Blocked** | Waiting for another session. It may block others too, further down the chain. |
   | **Active / Running** | Running normally |
   | **Sleeping** | Connected but idle |

4. **Hover a node** for login, host, program, status, open transactions, what blocks it, its wait and the start of its SQL.
5. **Click a node** to open **Session Details**: status, the session blocking it (`blocked`, `blocked_by`), the **head of its chain** (`head_blocker`) and how many sessions it blocks directly (`blocking`), wait type, wait time and wait resource, database, CPU, I/O, memory, login time, last batch, **open transactions** (`open_tran`), program, host, login and command. Below that is the **input buffer / query text**. **Copy SQL** copies the statement.
6. **Blocking chains only** hides every session that is neither blocking nor blocked. **Search SPID** shows matching sessions with their whole chain, so the head stays on screen. Zoom with **Ctrl + mouse wheel**; **Reset** returns to 100 %.

The **Process Map** chart on Live Metrics is the same view, smaller.

### Reading it

A head blocker is often a session that is **sleeping with an open transaction**: *status* is `sleeping` and *open_tran* is 1 or more. An application started a transaction and never committed it. It has no running request, so its query text is the **last batch it sent**, which usually tells you which part of the application to look at. Killing it (`KILL <spid>` in SSMS) is a decision for the DBA. SqlVitals never kills sessions.

Special cases:

- **Blocked by a negative number.** The blocker isn't a session. `blocked_by` says which kind: `-2` an orphaned distributed transaction, `-3` a deferred recovery transaction, `-4` / `-5` a latch whose owner SQL Server can't identify. The session is shown as blocked with nothing above it.
- **A session blocked by itself** (parallel queries on older builds report this) is not treated as blocking.
- **A circle of sessions blocking each other** is a deadlock the deadlock monitor hasn't resolved yet, usually within seconds. It is drawn from its lowest SPID and not listed as a head blocker. Once resolved, it appears on the [Deadlocks](Deadlocks.md) page with its victim.
- A blocker that isn't a user process (a system task) is still shown, so its chain has a head.

---

## Active Waits

> **Where:** sidebar → ⚡ Active Waits · **Use it when:** you want a list, rather than a map, of sessions that are waiting

A grid of every user request currently waiting, one row per session.

| Column | Meaning |
|---|---|
| **Session** / **Blocking** | The session ID, and the session blocking it (0 = not blocked) |
| **Wait Type** / **Category** | What it's waiting on, and its [category](Wait-Statistics.md#wait-categories) |
| **Wait (sec)** | How long it has been waiting |
| **Severity** | **Low** ≤ 1 s · **Medium** > 1 s · **High** > 10 s · **Critical** > 60 s |
| **Status**, **Command**, **Login**, **Database** | What the session is doing and who it is |
| **CPU (ms)**, **Logical Reads** | Work done so far |

Press **↻ Refresh** for a new snapshot.

---

### Under the hood

- **Live Metrics** reads `sys.dm_os_performance_counters` and `sys.dm_os_ring_buffers` in one lightweight query per interval. On Azure SQL Database, CPU comes from `sys.dm_db_resource_stats` instead (the portal's *CPU percentage*, a share of the service tier's limit, per 15 seconds), so it can jump between 0 and a high figure where the portal's one-minute average looks smoother. Rates such as batch requests/sec are computed from the difference between two samples, so the first sample after a start has nothing to compare with.
- The collector belongs to the connection (`MonitoringSession`), not to the page. Leaving the page doesn't stop it. The page keeps the last 60 samples in memory, and every sample is also written to the [history database](Monitoring-History-Database.md) (table `metric_samples`).
- **Active Waits** reads `sys.dm_exec_requests` joined to `sys.dm_exec_sessions` and `sys.dm_exec_sql_text`, user sessions only, excluding SqlVitals' own session. Query text is cut to 500 characters.
- **Processes** uses `GetProcessesAsync`: `sys.dm_exec_sessions` joined to `sys.dm_exec_requests`, user sessions plus any session blocking one. A session without a request gets its text from `sys.dm_exec_connections.most_recent_sql_handle` and its open transactions from the session. `Engine/Monitoring/BlockingChains` marks head blockers and builds the trees; `Controls/ProcessMapControl` draws them. It runs only while the page (or the Live Metrics *Process Map*) is on screen.
- All queries run under `READ UNCOMMITTED` so monitoring never waits on the locks it's reporting. See [Server Impact](Server-Impact-and-Permissions.md).
