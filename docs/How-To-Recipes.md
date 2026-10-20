# How-To Recipes

Step-by-step guides for common situations. You don't need to know which screen to open. Start from the problem.

- [Respond to an alert](#respond-to-an-alert)
- [Is this server OK?](#is-this-server-ok)
- [The server is slow right now](#the-server-is-slow-right-now)
- [Users say the application is hanging](#users-say-the-application-is-hanging)
- [Find which login has the most sessions](#find-which-login-has-the-most-sessions)
- [Spot a connection-pool leak](#spot-a-connection-pool-leak)
- [Users get deadlock errors (1205)](#users-get-deadlock-errors-1205)
- [A scheduled job failed or is taking too long](#a-scheduled-job-failed-or-is-taking-too-long)
- [Are my backups meeting the RPO?](#are-my-backups-meeting-the-rpo)
- [Is the storage slow?](#is-the-storage-slow)
- [Is this server set up right?](#is-this-server-set-up-right)
- [Who has the keys to this server?](#who-has-the-keys-to-this-server)
- [Something happened overnight](#something-happened-overnight)
- [A query got slower after a release](#a-query-got-slower-after-a-release)
- [Find a query that's sometimes slow (parameter sniffing)](#find-a-query-thats-sometimes-slow-parameter-sniffing)
- [Check a forced plan still works](#check-a-forced-plan-still-works)
- [Find which stored procedure costs the most](#find-which-stored-procedure-costs-the-most)
- [Is today normal?](#is-today-normal)
- [CPU is high](#cpu-is-high)
- [Plan routine index and statistics maintenance](#plan-routine-index-and-statistics-maintenance)
- [TempDB or the log is filling up](#tempdb-or-the-log-is-filling-up)
- [Find out what an application is calling](#find-out-what-an-application-is-calling)
- [Get a second opinion from an AI assistant](#get-a-second-opinion-from-an-ai-assistant)
- [Keep an eye on several servers](#keep-an-eye-on-several-servers)
- [Run SP Trace without monitoring the server](#run-sp-trace-without-monitoring-the-server)
- [Monitor my servers all the time](#monitor-my-servers-all-the-time)
- [Keep watching when my PC is off](#keep-watching-when-my-pc-is-off)
- [Get alerts in a Teams channel](#get-alerts-in-a-teams-channel)
- [Find out why there's a gap in the history](#find-out-why-theres-a-gap-in-the-history)
- [Keep personal data out of the history](#keep-personal-data-out-of-the-history)
- [Find out which permissions a login is missing](#find-out-which-permissions-a-login-is-missing)
- [Give a wide grid the whole screen](#give-a-wide-grid-the-whole-screen)
- [Go straight to a screen, server or setting](#go-straight-to-a-screen-server-or-setting)

---

## Respond to an alert

1. Click the **Windows notification**, or press **Investigate** on the alert: on the server's Overview, on its tile in **All servers**, or in the **Evidence** column of 🔔 Alerts.
2. The page that shows why opens **already filtered**: the head blocker's chain on Processes, the queries that used the most CPU since the alert began, the database whose log is filling up. The banner at the top says what you're looking at: *Opened from alert · Blocking · critical · since 13:58 — showing the blocking chain of session 87 only*.
3. Look closer from there: on Processes, a session's **Active waits for this session →** shows what it and the sessions it blocks are waiting on.
4. **Show all** clears the filter; **← Back** returns to where you clicked.
5. **The alert has already ended?** The banner says the page shows the server now, and offers **Open Live charts for** the alert's window (15 minutes either side), read from the monitoring history.

See [From an alert to its evidence](Alerts.md#from-an-alert-to-its-evidence) for the page each alert opens.

## Is this server OK?

1. Open the server: **All servers → Open** on its tile. It opens on its **Overview**.
2. **Active alerts**, at the top: each says what is happening in plain words and since when. Press **Investigate →** for its evidence, already filtered to the problem (see [Respond to an alert](#respond-to-an-alert)).
3. **What is costing points**: each check lowering the score, its points and why, with a link to the page that explains it. No alert yet doesn't mean nothing is wrong: a check can cost points before it has lasted long enough to alert.
4. **Since you last looked**: deadlocks, failed Agent jobs and regressed queries since you last opened this Overview. Each row links to its page.
5. The **charts** show when it started: pick **1h**, **24h** or **7d** in the top bar; dashed lines mark where alerts started.

See [Your Servers → Overview](Your-Servers.md#a-servers-overview).

## The server is slow right now

1. **The server's Overview**, or **💓 Live charts** for the full-size charts: which colour dominates the **Waits** chart? Is **CPU** high?
2. Follow the dominant wait:

   | Dominant wait | Go to |
   |---|---|
   | **Locks** | [Users say the application is hanging](#users-say-the-application-is-hanging) |
   | **CPU** | [CPU is high](#cpu-is-high) |
   | **I/O** | 🔥 Resource Queries → **By Logical Reads**, then 🔍 Index Health → Missing Indexes |
   | **Memory** | 🧠 Memory → look for **Pending** grants |
   | **Network** | The application is slow to read results. Look at what it's fetching (🛰️ SP Trace). |

3. **🔗 Processes → Waiting** lists exactly which sessions are waiting, and on what.

## Users say the application is hanging

This is usually **blocking**.

1. Check the **health dot**. Hover it to see the *Blocking* value.
2. Open **🔗 Processes**. The line above the map lists each **head blocker** with how many sessions wait behind it. Click the largest one; tick **Blocking chains only** to hide everything else.
3. In the **details drawer** at the right (Ctrl+J), check its status and open transactions, **Transaction open** (how long), program, host and the last statement.
4. **Sleeping with an open transaction** = an application opened a transaction and didn't commit. Give the statement and program name to the application team.
5. If it must be stopped now, **KILL script (review)** in the drawer gives the statement to copy into SSMS, with what it blocks and what to check first. The transaction will roll back, which can take a while. SqlVitals never runs it.
6. When the blocked sessions and the head belong to different applications, switch to **Grid**: the group headers show which login holds the head blocker, and clicking **Head of chain** on a blocked row jumps to it.

## Find which login has the most sessions

1. Open **🔗 Processes** and press **Grid**. **Group by login** is ticked.
2. Groups with a head blocker or a blocked session come first, then the rest **by session count**, most first. Each header reads like `CORP\svc-orders · 142 sessions (3 running, 139 sleeping) · CPU 12,340 ms`.
3. **Collapse all**, then read the headers down the page: that's the server's sessions by login, busiest first.
4. To compare logins on load rather than count, expand a group and **sort by CPU (ms)** or **Physical I/O**; or untick **Group by login** and sort the whole list.
5. Type a login, host or program in **Filter rows…** to see only its sessions; the headers then sum up just those.

## Spot a connection-pool leak

An application that opens connections and doesn't close them fills its pool with idle sessions, until it hits the pool's limit (100 by default in .NET) and new requests time out.

1. Open **🔗 Processes** → **Grid**, and press **Start** so it keeps refreshing.
2. Look for a login with far more sessions than the others, **nearly all sleeping**: `CORP\svc-orders · 300 sessions (2 running, 298 sleeping)`.
3. Expand it and **sort by Last batch**. Many sessions whose last batch ended long ago, from the same **Host** and **Program**, are connections the application is holding and not using.
4. Click the **Active** and **Blocked** chips to hide those states and leave only **Sleeping**, then watch the count over a few refreshes. A number that only goes up is a leak; one that rises and falls with traffic is a pool doing its job.
5. Check **Open tran**: sleeping sessions with an open transaction also hold locks, and can become [head blockers](#users-say-the-application-is-hanging).
6. Give the application team the login, host, program and the **Query text** of a few idle sessions: it's their last batch, which points at the code that didn't close its connection.

## Users get deadlock errors (1205)

*"Transaction … was deadlocked on lock resources with another process and has been chosen as the deadlock victim."*

1. Open **💥 Deadlocks**. There's nothing to switch on first: SQL Server's system_health session has been recording them all along.
2. Go to the **Recurring** tab. The top pattern is the one that happens most. Press **Open newest**.
3. In **Participants**, compare **Waiting for** and **Holding**. Two sessions touching the same tables in opposite order is the classic case.
4. Select each participant to read its statement and input buffer, and note the **Application** and **Host**.
5. **Save as .xdl…** and open it in SSMS for the graph, or attach it to the ticket for the application team.
6. See [Deadlocks → Reading it](Deadlocks.md#reading-it) for the usual fixes: the same lock order in both code paths, a supporting index, shorter transactions.

## A scheduled job failed or is taking too long

*The nightly backup, index maintenance or ETL load didn't finish, or is still running.*

1. Open **🕒 Agent Jobs**. Jobs **running long** (amber) and **failed** (red) are at the top; *Show → Needs attention* hides the rest.
2. If there's an amber line saying **SQL Server Agent isn't running**, that's the problem: no job starts until it's started again.
3. For a **failed** job, select it. The newest run's message names the **failing step and its error**; select the step for its full message.
4. For a job **running long**, compare **Duration** with **Average**. Then open **🔗 Processes**: its session (program *SQLAgent - TSQL JobStep*) may be blocked by another.
5. **Failed runs** like *5 of 30* means it fails often, not just today. Read the older failed runs' messages too.
6. See [Agent Jobs → Reading it](Agent-Jobs.md#reading-it) for the usual causes: a dropped owner login, a full backup disk, blocking.

## Are my backups meeting the RPO?

*An audit asks, a restore is on the horizon, or you just want to know every database is covered.*

1. Open **💾 Backups**. Set the two ages to your RPO: how old a **full** backup may get (e.g. `26h` with nightly fulls) and a **log** backup (e.g. `30 min` with 15-minute log backups). Press **Enter**.
2. **Red** rows have nothing to restore (*No full backup*), or no log backups in FULL recovery (*No log backup*, *Log chain broken*). Fix those first.
3. **Amber** rows have a backup older than the ages you set. Open **🕒 Agent Jobs**: the backup job has usually failed, or is still running.
4. **Data at risk** is how much work each database would lose right now. Sort by it to see the worst.
5. Select a database for its backup history: check **Checksum** says *Yes* and the files go where you expect.
6. If an amber line mentions an **availability group**, look at the replica that takes the backups too. See [Backups → Reading it](Backups.md#reading-it).
7. To hear about it without opening the page, monitor the server: from 0.76.0 the *Backups past RPO* check turns its dot red, and raises an [alert](Alerts.md), when a database falls past these ages.

## Hear about a full disk, a missed backup or a failed job

*Overnight problems show up in the morning, after the disk filled or the backup didn't run.*

1. [Monitor the server](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring), and for nights and weekends keep it watched: [the collector service](Collector-Service.md), or SqlVitals left running in the tray.
2. From 0.76.0 three checks are read every 5 minutes: **Disk free** (each volume holding database files), **Backups past RPO** (the ages on **💾 Backups**) and **Failed Agent jobs**. A problem turns the dot amber or red, lowers the score and raises an [alert](Alerts.md); send alerts to [Slack or Teams](Chat-Notifications.md) to hear about it away from the PC.
3. **Investigate** on the alert opens the page that explains it: **💽 File I/O** (its *Free* column), **💾 Backups** or **🕒 Agent Jobs**.
4. A big volume that is low only as a share (a 4 TB disk at 12 % still has 480 GB) isn't flagged: a volume must be below both the % and the GB. Change either in [Settings → Health thresholds](Health-Dot-and-Background-Monitoring.md#change-the-thresholds).
5. A grey bar means the check couldn't be read: hover it for why, usually a missing permission on msdb (see [Server impact and permissions](Server-Impact-and-Permissions.md#permissions)).

## Is the storage slow?

*Waits show `PAGEIOLATCH_*` or `WRITELOG` (see [Wait Statistics](Wait-Statistics.md)), or the storage team asks whether SQL Server is seeing slow disks.*

1. Open **💽 File I/O** and wait one interval (10 s) for the first figures. Files over the threshold are at the top: **red** at five times it, **amber** over it.
2. Set the thresholds to suit the storage: e.g. `10` ms for data and `2` ms for log files on all-flash, the defaults (20 and 10) for a typical SAN. Press **Enter**.
3. Look at the **Volumes** grid. Several slow files on one volume point at the storage: take the volume and the times to the storage team. One slow file on a fine volume points at that database's workload.
4. **Log file** slow on writes: every commit waits for it. It needs fast storage of its own.
5. **Data file** slow on reads with large **KB/read**: scans. Find them in [Resource Queries](Query-Performance.md#resource-queries) by reads.
6. To measure a particular job or report: choose **Latency over → Since the first reading**, press **↺ Start over**, run it, then read the page. See [File I/O → Reading it](File-IO.md#reading-it).

## Is this server set up right?

*You've taken over a server, just installed or upgraded one, or an audit is coming.*

1. Open **✅ Config Checks**. Warnings are at the top, amber.
2. Start with the server settings: **Max server memory** not set, then **MAXDOP** and **Cost threshold for parallelism**. They take effect at `RECONFIGURE`, without a restart.
3. **TempDB data files** and **file sizes**: add or grow files so there's one per processor up to 8, all the same size. Check the drive has room first.
4. **Auto-shrink** and **Page verify** are safe to fix at any time.
5. **Compatibility level** changes query plans: turn Query Store on first and change it at a quiet time; watch [Query Regressions](Query-Regressions.md) afterwards.
6. **📝 Script the warnings** gives one script for all of them: review it, run the parts you agree with, then press **↻ Check again**. See [Configuration Checks → Reading it](Configuration-Checks.md#reading-it).

## Who has the keys to this server?

*You've taken over a server, a security review is coming, or a database was just restored from another server.*

1. Open **🔒 Security Audit**. Risks are at the top, red. If an amber line says the login can't see everything, ask for `VIEW ANY DEFINITION`.
2. **sa login**: if it's enabled, find what signs in as it, give that its own login, then disable sa.
3. **sysadmin members**: every risk is a whole group of people or a password login with the keys to the server. Replace a local group with a group of the DBAs; give an application's login only what it uses. Then **Review** the rest: does each person and group still need it?
4. **TRUSTWORTHY**: for each database, find what needs it (CLR assemblies, cross-database code), sign that with a certificate, then turn it off.
5. **Orphaned users**: after a restore from another server, copy the logins with their SIDs; remap a user to a login of its name; drop the users nobody needs.
6. **📝 Script the risks** gives one script for all of them: review it, run the parts you agree with, then press **↻ Audit again**. See [Security Audit → Reading it](Security-Audit.md#reading-it).

## Something happened overnight

1. History only exists for times the connection was **monitored**. To have it next time, see [Monitor my servers all the time](#monitor-my-servers-all-the-time).
2. **💓 Live charts → Custom…**: set From/To around the incident, and press **Show**. Look for the CPU or wait spike.
3. **📊 Waits → same range**: the table ranks the waits of that range, and **Top 20** charts them: which wait types rose.
4. **⏱️ Perfmon → same range**: tick counters such as batch requests, lock waits or page life expectancy.
5. **🐢 Query Regressions**: *Recent: last 24 hours*, to see if a query got slower.

## A query got slower after a release

1. **🐢 Query Regressions**. Set **Recent** to cover the time since the release, e.g. *last 4 hours*.
2. **Compared with: The 7 days before**.
3. Sort by **Extra time ms**. That's the real impact.
4. **Plan change = New plan**? Click **Now** and **Before** to open both [graphical plans](Execution-Plans.md) side by side. Look for a scan where there was a seek, a different join type, or a new **⚠** (spill, missing statistics). Save both as `.sqlplan` to keep them.
5. Quick fix: force the old plan in SSMS's Query Store reports (then [check it still works](#check-a-forced-plan-still-works)). Proper fix: find why the plan changed. Check 📉 **Stale Stats**, parameter sniffing, or new or dropped indexes.

## Find a query that's sometimes slow (parameter sniffing)

1. **📋 Query Store** → **High Variation**. Pick a **Period** that covers the complaints, e.g. *Last 7 days*.
2. Keep **Metric: Duration**. The top rows vary most between executions; **Variation** 3× or more is a lot.
3. **Plans** highlighted (more than one)? That's the usual sign of [parameter sniffing](Glossary.md): a plan built for one parameter value, reused for values it doesn't suit.
4. Double-click the row for **Query details**. The chart has a line per plan: one plan fast, another slow, points to the plan rather than the load. **View Plan** on each and compare (e.g. a seek against a scan, or a much smaller estimate).
5. Fixes, in order of preference: an index that suits every value; `OPTION (RECOMPILE)` on a cheap query; `OPTIMIZE FOR`; a [Query Store hint](Query-Performance.md#forced-plans) (SQL Server 2022+); forcing the good plan in SSMS. SqlVitals changes nothing itself.

## Check a forced plan still works

1. **📋 Query Store**. The **Forced plans** card says how many there are; click it for the **Forced Plans** tab.
2. A row in amber (**Warning**) has **Failures**: SQL Server couldn't use the plan. **Last failure reason** says why, e.g. `NO_INDEX` when an index the plan uses was dropped or disabled.
3. **Executions** far below the query's own (double-click for **Query details**) also means the forced plan isn't being used.
4. Put the index back, or stop forcing: **Copy unforce** copies `EXEC sys.sp_query_store_unforce_plan …` to review and run yourself.
5. **Forced by: Automatic plan correction** means automatic tuning forced it after a regression; it unforces it again if it stops helping.

## Is today normal?

1. Open **💓 Live charts**, **📊 Waits** or **⏱️ Perfmon**.
2. Tick **Compare to baseline**. Dashed lines show **the same time last week**.
3. Current line well above the dashed line = unusual. Both similar = that's just your normal load.

## Azure SQL Database is slow, but CPU is low

1. Open **💓 Live charts** and look at the **Azure resources** card (from 0.82.0). If **Log IO** or **Data IO** is at or near 100 %, Azure is throttling the database at its service tier's limit. **DTU**, on a DTU tier, is the highest of the three, so it is at 100 % too.
2. **Log IO** at the limit: something writes a lot. **🔗 Processes** shows what is running; large batches, index rebuilds and loads are the usual cause. Spread them out, write in smaller batches, or move to a tier with a higher log rate.
3. **Data IO** at the limit: reads that don't fit in memory. **🔥 Resource Queries → By Logical Reads** shows the queries reading most; an index often fixes it.
4. The **Azure IO** health check turns amber at 80 % and red at 95 %, and raises an [alert](Alerts.md), so you hear about it next time.

## CPU is high

1. **🔥 Resource Queries → By CPU**. Sort by *Total CPU µs* (total cost) or *Avg CPU µs* (per run).
2. **View Plan** on the top queries. In the [graphical plan](Execution-Plans.md), look at the highlighted (≥ 25 % cost) operators: scans on big tables, big sorts, and **⚠** warnings. A green missing-index hint is worth reviewing.
3. **🔎 Plan Cache Health → Implicit Conversions**: data-type mismatches cause scans that burn CPU.
4. **🔎 Plan Cache Health → Multi-Plan Queries**: many compilations cost CPU too.
5. **📦 DB Storage → Max DOP / Cost Threshold**: a cost threshold of 5 (the default) often causes too much parallelism (`CXPACKET` waits). **✅ Config Checks** says what to set them to for this server.

## Find which stored procedure costs the most

1. **🔥 Resource Queries**, then tick **Group by object** beside Row Limit. Each procedure, function and trigger is one closed line with the totals of its statements in the list, the costliest first; the ad hoc queries are one more.
2. **By Logical Reads** ranks them by reads, **By CPU** by CPU. A procedure with many medium statements can outrank one big ad hoc query.
3. Click the top group to open it and see which of its statements cost the most. **View** shows a statement's whole text; **View Plan** its plan.
4. Raise **Row Limit** (up to 200) if a procedure's statements fall outside the list: the totals count only the statements listed.

## Plan routine index and statistics maintenance

1. **🔍 Index Health → Fragmentation**. Keep *Min Page Count* at 1000, then **Generate Maintenance Script**.
2. **📉 Stale Stats**. Sampling *Default*, then **Generate UPDATE STATISTICS Script**.
3. **🔍 Index Health → Unused Indexes**. Check how long the server has been up **first**. Review candidates with the application team before you **Generate DROP Script**.
4. **🔍 Index Health → Missing Indexes**. Filter by table, and review overlapping suggestions before you **Generate CREATE Script**.
5. **Save as .sql**, test on a non-production copy, and run in a maintenance window. SqlVitals never runs scripts for you.

## TempDB or the log is filling up

- **TempDB:** **🗄️ TempDB → Top Sessions by TempDB Allocation**. *Internal KB* = sorts and hashes from big queries; *User Obj KB* = temp tables. Find the session and its query.
- **Log:** hover the **health dot**. The *Log space used* tooltip names the database. Then check **📦 DB Storage → Database Files** for its recovery model and log size. A full log in *FULL* recovery usually means log backups aren't running.

## Find out what an application is calling

1. **🛰️ SP Trace**. Set **Min duration** (e.g. 0 on a quiet test server, 100+ on production).
2. **● Start Trace**, then use the application.
3. Filter by **Application** or **Host**. Untick **Hide sp_executesql** if the application uses an ORM.
4. **By Procedure** summarises what was called and what it cost.
5. **■ Stop Trace** when done.

## Get a second opinion from an AI assistant

1. **📤 Export / AI** → **Select All** (or pick the groups related to your problem).
2. **📤 Export Now**, and open the file.
3. **Review it for sensitive data** (query text may contain real values).
4. Paste it into your assistant with a clear question and the symptoms you've seen.

## Keep an eye on several servers

1. Add each server in **⚙️ Settings → Connections** with **Monitor in background** on (its *Monitoring & alerts* tab), and turn on **Settings → Monitoring & history → Start monitoring automatically** (see [Monitor my servers all the time](#monitor-my-servers-all-the-time)).
2. Open **All servers**: a tile per server, the ones that need attention first, each with its score, its last hour and a bar per check. **Open** a tile for its [Overview](Your-Servers.md#a-servers-overview): its alerts, what is costing points with links to the pages that explain each check, and what happened since you last looked. See [Your Servers](Your-Servers.md). A tile's **⋯ → Why this score…** shows which checks lowered it, without opening the server.
3. Open **🔔 Alerts** to see what went wrong while you weren't looking, and when. Filter by connection. See [Alerts](Alerts.md).
4. Servers that always run hot: turn on **Use its own health thresholds** on their *Health thresholds* tab so they don't cry wolf.
5. Use **Settings → Monitoring & history** to keep 14+ days, so you can compare against last week, and keep the alerts that long.

## Run SP Trace without monitoring the server

With **Start monitoring automatically** off (the default for a new installation from 0.46.0), SqlVitals sends nothing to the server except what you ask for.

1. Check **⚙️ Settings → Monitoring & history**: **Start monitoring automatically** is unticked.
2. Open SqlVitals and connect. The server's Overview says it isn't monitored; leave it.
3. Open **🛰️ SP Trace** and trace as usual. See [Find out what an application is calling](#find-out-what-an-application-is-calling).
4. If the server's dot in the sidebar is coloured, it's being monitored: press **■** in its header to stop it until SqlVitals exits.

Nothing else runs beside the trace: no Live Metrics query, history snapshot or Process Map polling. See [SP Trace → Impact on the server](SP-Trace.md#impact-on-the-server).

## Monitor my servers all the time

1. **⚙️ Settings → Monitoring & history**: tick **Start monitoring automatically**. The active connection starts straight away.
2. For each other server to watch while another is active: **Settings → Connections → Edit**, turn on **Monitor in background** on the *Monitoring & alerts* tab, and **Save**.
3. Tick **Settings → Notification area → Keep SqlVitals running in the notification area…**, so closing the window doesn't stop monitoring.
4. Keep **Settings → Monitoring & history** at 14 days or more, for week-over-week baselines.

From then on every start of SqlVitals monitors them, and raises [alerts](Alerts.md) and Windows notifications. A **■** press in the sidebar, on a tile's **⋯** menu, on a server's Overview or on Live charts still stops one until SqlVitals exits.

## Keep watching when my PC is off

The app watches only while it runs. To catch a 3 a.m. problem with your laptop closed, run the optional [collector service](Collector-Service.md):

1. Unzip **SqlVitals-Collector-&lt;version&gt;-win-x64.zip** from the release to `C:\Program Files\SqlVitals Collector` on this PC (one that stays on), or on an always-on server.
2. In an administrator command prompt there: `SqlVitals.Collector.exe install --start`. It prints the `CREATE LOGIN` / `GRANT VIEW SERVER STATE` to run on each server.
3. On this PC, press **▶ Start** on a server and choose **Watch with the collector service**, or use **⚙️ Settings → Collector service → Hand over**. Its chat channels go with it. On a server, use `SqlVitals.Collector.exe add-server --server sql01 --name Sales-Prod` and `add-channel --name "DBA team" --heartbeat 6h` instead. Then `set --share-copy-minutes 5`, and enter the shared folder in **Settings → Collector service → A collector on another PC**.
4. `SqlVitals.Collector.exe test`, then `list`. The service picks up changes within 10 seconds.

Turn on a [heartbeat with a ping URL](Chat-Notifications.md#heartbeat), so you also hear when the collector PC itself goes down.

## Get alerts in a Teams channel

1. In Teams, go to the channel → **•••** → **Workflows** → **Post to a channel when a webhook request is received**. Name it, pick the team and channel, **Add workflow**, and copy the URL. (Slack instead: [Chat Notifications → Slack](Chat-Notifications.md#slack).)
2. **⚙️ Settings → Alerts & chat → Chat channels → + Add channel**: a name such as *DBA team (Teams)*, paste the URL, and **Save channel**. Microsoft Teams is picked from the URL.
3. Choose **Connections** (all monitored ones, or only some) and **Post** (warnings and critical, or critical only).
4. Click **Send test** on the channel's card. *Accepted by the workflow* means Teams took it: check that the card appears in the channel.
5. Make sure the servers are monitored ([Monitor my servers all the time](#monitor-my-servers-all-the-time)) and SqlVitals keeps running in the notification area.

From then on, an alert that starts or turns critical is posted to the channel within seconds, while SqlVitals runs on this PC. See [Chat Notifications](Chat-Notifications.md) for what is posted and what each test result means.

## Find out why there's a gap in the history

1. **⚙️ Settings → Activity log & diagnostics**. Set **Period** to cover the gap.
2. Set **Event** to *Monitoring stopped*, or leave *All events* to see everything around it.
3. The row before the gap says why: **Source** *user: sidebar* or *user: Home* (someone pressed ■; *user: connection selector* before 0.57.0), *setting changed* (Start monitoring automatically was turned off), *connection edited*, or *app exit*. **Windows user** and **PC** say who.
4. No *App exited* before the gap, and the next *App started* says *The previous run didn't exit cleanly*: SqlVitals was killed, crashed or the PC turned off.
5. A *History cleared* row means the history before it was deleted on purpose.

The same, as a query on the history file: [Monitoring History Database → Example queries](Monitoring-History-Database.md#example-queries).

## Keep personal data out of the history

For an approver who asks what SqlVitals keeps on disk (from 0.77.0):

1. **⚙️ Settings → Monitoring & history → Store query text in history**: **Without literals** keeps each query's shape with its values replaced by `?`; **Off** keeps no text at all. CPU, reads and executions are kept either way.
2. Text saved before the change is still there: press **Clear stored query text…** in the same place, and confirm. The collector service on this PC, if there is one, clears its own too; on its PC, `SqlVitals.Collector clear-query-text`.
3. **Activity log & diagnostics** shows a *Query text cleared* row, with who and when, for the record.
4. For what the remaining files hold and who can read them, give the approver [Security and Privacy](Security-and-Privacy.md).

## Find out which permissions a login is missing

Before asking a DBA, or when a page says it lacks a permission (from 0.78.0):

1. **⚙️ Settings → Connections**, then **Check permissions** on the connection's card. On a first launch, the *Check* step does the same.
2. Read the list: each area is **✓ Granted**, **✗ Missing** or *Not needed*, with the pages and checks that need it. Server state is the one that matters most: without it nothing can be monitored.
3. **Copy script** and send it to the DBA. It only grants reads, for the login as the server knows it.
4. Once it's run, **Check again**. The pages pick the new permissions up the next time they read. See [Check permissions](Connections.md#check-permissions).

## Give a wide grid the whole screen

1. Press **Ctrl+B** once to collapse the sidebar to icons (56 px), or twice to hide it: the grid gets the whole width, 1320 px in the default window instead of 1088.
2. Still need a screen? Collapsed, point at a group's icon to see its screens. Hidden, click **☰** at the top left, or rest the pointer on the window's left edge.
3. **Ctrl+B** again pins the sidebar back. Whichever state you leave it in is used next time. See [Pin, collapse or hide the sidebar](Getting-Started.md#pin-collapse-or-hide-the-sidebar).

## Go straight to a screen, server or setting

1. Press **Ctrl+K**, from any screen.
2. Type a few letters of what you want: a screen (*temp*), a server (*prod*), a setting (*rpo*), or what you're after (*blocking* finds Processes).
3. Press **Enter** to open the first result, or **↑ / ↓** to pick another first.
4. **The same screen on another server:** on Processes, type the other server's name and press **Enter**: its Processes opens.
5. **← Back** returns to where you were. Before you type, the list is the screens you opened last. See [Jump to a server, screen or setting](Getting-Started.md#jump-to-a-server-screen-or-setting).