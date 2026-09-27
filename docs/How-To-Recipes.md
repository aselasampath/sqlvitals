# How-To Recipes

Step-by-step guides for common situations. You don't need to know which screen to open. Start from the problem.

- [The server is slow right now](#the-server-is-slow-right-now)
- [Users say the application is hanging](#users-say-the-application-is-hanging)
- [Users get deadlock errors (1205)](#users-get-deadlock-errors-1205)
- [A scheduled job failed or is taking too long](#a-scheduled-job-failed-or-is-taking-too-long)
- [Are my backups meeting the RPO?](#are-my-backups-meeting-the-rpo)
- [Is the storage slow?](#is-the-storage-slow)
- [Is this server set up right?](#is-this-server-set-up-right)
- [Who has the keys to this server?](#who-has-the-keys-to-this-server)
- [Something happened overnight](#something-happened-overnight)
- [A query got slower after a release](#a-query-got-slower-after-a-release)
- [Is today normal?](#is-today-normal)
- [CPU is high](#cpu-is-high)
- [Plan routine index and statistics maintenance](#plan-routine-index-and-statistics-maintenance)
- [TempDB or the log is filling up](#tempdb-or-the-log-is-filling-up)
- [Find out what an application is calling](#find-out-what-an-application-is-calling)
- [Get a second opinion from an AI assistant](#get-a-second-opinion-from-an-ai-assistant)
- [Keep an eye on several servers](#keep-an-eye-on-several-servers)

---

## The server is slow right now

1. **💓 Live Metrics**: which colour dominates the **Waits** chart? Is **CPU** high?
2. Follow the dominant wait:

   | Dominant wait | Go to |
   |---|---|
   | **Locks** | [Users say the application is hanging](#users-say-the-application-is-hanging) |
   | **CPU** | [CPU is high](#cpu-is-high) |
   | **I/O** | 🔥 Resource Queries → **By Logical Reads**, then 🔍 Index Health → Missing Indexes |
   | **Memory** | 🧠 Memory Grants → look for **Pending** grants |
   | **Network** | The application is slow to read results. Look at what it's fetching (🛰️ SP Trace). |

3. **⚡ Active Waits** lists exactly which sessions are waiting, and on what.

## Users say the application is hanging

This is usually **blocking**.

1. Check the **health dot**. Hover it to see the *Blocking* value.
2. Open **🔗 Processes**. The line above the map lists each **head blocker** with how many sessions wait behind it. Click the largest one; tick **Blocking chains only** to hide everything else.
3. In **Session Details**, check `status`, `open_tran`, `program_name`, `host_name` and the query text.
4. **Sleeping with `open_tran` ≥ 1** = an application opened a transaction and didn't commit. Give the query text and program name to the application team.
5. If it must be stopped now, a DBA can run `KILL <spid>` in SSMS. The transaction will roll back, which can take a while.

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

1. Make sure the connection has **Monitor in background** on. History only exists for monitored connections.
2. **💓 Live Metrics → Custom…**: set From/To around the incident, and press **Show**. Look for the CPU or wait spike.
3. **📈 Wait Trend → same range → Top Waits**: which wait types rose.
4. **📈 Perfmon → same range**: tick counters such as batch requests, lock waits or page life expectancy.
5. **🐢 Query Regressions**: *Recent: last 24 hours*, to see if a query got slower.

## A query got slower after a release

1. **🐢 Query Regressions**. Set **Recent** to cover the time since the release, e.g. *last 4 hours*.
2. **Compared with: The 7 days before**.
3. Sort by **Extra time ms**. That's the real impact.
4. **Plan change = New plan**? Click **Now** and **Before** to open both [graphical plans](Execution-Plans.md) side by side. Look for a scan where there was a seek, a different join type, or a new **⚠** (spill, missing statistics). Save both as `.sqlplan` to keep them.
5. Quick fix: force the old plan in SSMS's Query Store reports. Proper fix: find why the plan changed. Check 📉 **Stale Stats**, parameter sniffing, or new or dropped indexes.

## Is today normal?

1. Open **💓 Live Metrics**, **📈 Wait Trend** or **📈 Perfmon**.
2. Tick **Compare to baseline**. Dashed lines show **the same time last week**.
3. Current line well above the dashed line = unusual. Both similar = that's just your normal load.

## CPU is high

1. **🔥 Resource Queries → By CPU**. Sort by *Total CPU µs* (total cost) or *Avg CPU µs* (per run).
2. **View Plan** on the top queries. In the [graphical plan](Execution-Plans.md), look at the highlighted (≥ 25 % cost) operators: scans on big tables, big sorts, and **⚠** warnings. A green missing-index hint is worth reviewing.
3. **⚠️ Implicit Conv.**: data-type mismatches cause scans that burn CPU.
4. **🔎 Plan Cache Health → Multi-Plan Queries**: many compilations cost CPU too.
5. **🗄️ DB Storage → Max DOP / Cost Threshold**: a cost threshold of 5 (the default) often causes too much parallelism (`CXPACKET` waits). **✅ Config Checks** says what to set them to for this server.

## Plan routine index and statistics maintenance

1. **🔍 Index Health → Fragmentation**. Keep *Min Page Count* at 1000, then **Generate Maintenance Script**.
2. **📉 Stale Stats**. Sampling *Default*, then **Generate UPDATE STATISTICS Script**.
3. **🔍 Index Health → Unused Indexes**. Check how long the server has been up **first**. Review candidates with the application team before you **Generate DROP Script**.
4. **🔍 Index Health → Missing Indexes**. Filter by table, and review overlapping suggestions before you **Generate CREATE Script**.
5. **Save as .sql**, test on a non-production copy, and run in a maintenance window. SqlVitals never runs scripts for you.

## TempDB or the log is filling up

- **TempDB:** **🗄️ TempDB → Top Sessions by TempDB Allocation**. *Internal KB* = sorts and hashes from big queries; *User Obj KB* = temp tables. Find the session and its query.
- **Log:** hover the **health dot**. The *Log space used* tooltip names the database. Then check **🗄️ DB Storage → Database Files** for its recovery model and log size. A full log in *FULL* recovery usually means log backups aren't running.

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

1. Add each server in **⚙️ Settings** with **Monitor in background** ticked.
2. Open the sidebar selector and look at the **health scores**: the lowest one needs attention first. Click a score to see which checks lowered it, without switching connection.
3. Open **🔔 Alerts** to see what went wrong while you weren't looking, and when. Filter by connection. See [Alerts](Alerts.md).
4. Servers that always run hot: tick **Use its own health thresholds** so they don't cry wolf.
5. Use **Settings → Monitoring History** to keep 14+ days, so you can compare against last week, and keep the alerts that long.
