# Glossary

Plain-language definitions of the terms used in SqlVitals and this wiki.

| Term | Meaning |
|---|---|
| **Activity log** | SqlVitals' record, in the history file, of when monitoring was started and stopped and by whom or what, when the app started and exited, every settings and connection change with its old and new value (never a secret), and when history was cleared. Kept 365 days. See [Settings → Activity log](Settings.md#activity-log). |
| **Alert** | A record of one health indicator staying past its threshold for a few samples in a row: when it started and ended, and its worst value. See [Alerts](Alerts.md). |
| **Auto-shrink** | A database option that shrinks its files by itself whenever a quarter of them is free. It fragments indexes and the space is usually needed again, so it should be OFF. |
| **Azure SQL Database** | Microsoft's managed, single-database SQL service. Some server-level views aren't available on it, and SqlVitals adapts automatically. |
| **Azure SQL Managed Instance (MI)** | A managed, near-complete SQL Server in Azure. Behaves like on-premises for SqlVitals. |
| **Baseline** | A reference period to compare against. On trend pages: the same time last week. On Query Regressions: *the 7 days before* or *the same time last week*. |
| **Blocking** | One session waiting for a lock held by another. A **blocking chain** is a session and everything waiting behind it; the **head blocker** is the session at its root, blocking others and waiting on nothing. See [Processes](Live-Monitoring.md#processes-blocking-chains). |
| **Buffer cache hit ratio** | % of page reads served from memory instead of disk. Should be close to 100 %. |
| **Cardinality estimate** | The optimizer's guess of how many rows an operation returns. Bad guesses lead to bad plans. |
| **Clustered index / heap** | A clustered index stores the table in key order. A heap is a table without one. |
| **Collector** | SqlVitals' background task that reads Live Metrics for one monitored connection at an interval. |
| **Compatibility level** | A database setting that decides which SQL Server version's query optimizer and T-SQL behaviour it gets: 160 for SQL Server 2022, 150 for 2019. An upgraded database keeps its old level until it's changed. |
| **Connection** | A saved server and database in SqlVitals. |
| **CONTROL SERVER** | A server permission that gives almost everything **sysadmin** does. The [Security Audit](Security-Audit.md) lists who holds it alongside the sysadmin members. |
| **CONVERT_IMPLICIT** | SQL Server converting one data type to another in a comparison. It can prevent index seeks. |
| **Cost threshold for parallelism** | The estimated query cost above which SQL Server considers a parallel plan. The default of 5 is widely considered too low; 50 is a common starting point. See [Configuration Checks](Configuration-Checks.md). |
| **CXPACKET / CXCONSUMER** | Waits from parallel queries coordinating their threads |
| **Deadlock** | Two or more sessions each holding a lock another needs, so none can go on. SQL Server rolls one back (the **victim**, error 1205) so the others can finish. See [Deadlocks](Deadlocks.md). |
| **Deadlock graph (.xdl)** | A deadlock report saved as a file. SSMS opens it as a drawing of the processes and the locks they fought over. |
| **Detail snapshot** | SqlVitals' periodic (5-minute default) save of waits per type, file I/O, top queries, memory and counters |
| **DMV** | *Dynamic Management View*: SQL Server's built-in diagnostic views (`sys.dm_*`), which SqlVitals reads |
| **DPAPI** | Windows' data-protection API. SqlVitals uses it to encrypt saved connections so only your Windows account can read them. |
| **Estimated vs. actual plan** | An *estimated* plan (plan cache, Query Store) has SQL Server's guesses only. An *actual* plan was captured while the query ran and adds the real row counts and times. |
| **Execution plan** | The steps SQL Server chose to run a query: which indexes, joins and order. SqlVitals draws it graphically ([Execution Plans](Execution-Plans.md)); it can also be saved as `.sqlplan` for SSMS. |
| **Extended Events (XE)** | SQL Server's lightweight tracing system, used by SP Trace; the Deadlocks page reads the built-in *system_health* session |
| **Forced plan** | A plan Query Store makes a query use (`sp_query_store_force_plan`), by hand or by *automatic plan correction*. If forcing fails, e.g. because an index the plan uses was dropped (`NO_INDEX`), the query runs with whatever plan the optimizer picks. See [Query Store → Forced Plans](Query-Performance.md#forced-plans). |
| **Fragmentation** | How out of order an index's pages are. High fragmentation on large indexes can slow scans. |
| **FULLSCAN** | Updating statistics by reading every row: most accurate, most expensive |
| **Health dot** | The coloured indicator per connection: on its tile on All servers, in the sidebar's server header and in the breadcrumb |
| **Health score** | A 0–100 number per connection: 100 minus the points lost by health checks past their thresholds. See [Health score](Health-Dot-and-Background-Monitoring.md#health-score). |
| **Home** | The 🏠 page with a tile per saved connection: its health score and status, its last hour, a bar per check, the ones that need attention first. Each opens the server's details. See [Your Servers](Your-Servers.md). |
| **Implicit conversion** | See *CONVERT_IMPLICIT* |
| **I/O latency** | How long one read or write to a database file takes, in milliseconds. SQL Server records the total wait for each file's reads and writes (its **I/O stall**); divided by how many there were, it's the average latency. The [File I/O](File-IO.md) page works it out over the last few seconds. |
| **Key / RID lookup** | After an index seek, fetching the rest of the row from the clustered index (key) or heap (RID). Expensive when repeated many times. |
| **Latch** | A short internal lock on a memory structure. `PAGELATCH` = in-memory page; `PAGEIOLATCH` = page being read from disk: many of them point at slow [file I/O](File-IO.md). |
| **Log chain** | The unbroken series of log backups since a full backup, which a point-in-time restore replays. Switching a database to SIMPLE breaks it; a full or differential backup starts it again. |
| **Login** | The server-level identity a session connects as (`login_name`): a SQL login such as `sa`, or a Windows user or group such as `CORP\svc-orders`, often an application's service account. Different from a **database user**, which a login is mapped to inside each database and which is what permissions on tables are granted to. The [Processes grid](Live-Monitoring.md#grid-view) groups sessions by login. |
| **Logical reads** | Pages read from memory. The best measure of how much data a query touches. |
| **Max server memory** | The most memory SQL Server's buffer pool and caches may take (`sp_configure 'max server memory (MB)'`). Left at the default it has no limit, and Windows can run short. |
| **MAXDOP** | *Max degree of parallelism*: how many CPUs one query may use. [Configuration Checks](Configuration-Checks.md) works out the recommended value from the server's processors and NUMA nodes. |
| **Memory clerk** | A consumer of SQL Server memory, e.g. the buffer pool or plan cache |
| **Memory grant** | Workspace memory a query reserves for sorts and hashes before running. **Pending** = waiting for one. |
| **Missing index** | An index the optimizer noted would have helped a query |
| **msdb** | The system database where SQL Server Agent keeps its jobs, schedules and job history, and where SQL Server records every backup (`backupset`). The [Agent Jobs](Agent-Jobs.md) and [Backups](Backups.md) pages read it. |
| **NOLOCK / READ UNCOMMITTED** | Reading without waiting for locks. SqlVitals reads diagnostic views this way so it never gets blocked itself. |
| **NUMA node** | A group of processors with their own memory. Queries run best within one node, which is why the recommended MAXDOP depends on the processors per node. |
| **Orphaned user** | A SQL user in a database whose SID matches no login on the server, usually left by restoring the database on another server or dropping its login. See [Security Audit](Security-Audit.md#orphaned-users). |
| **Page life expectancy (PLE)** | Seconds a data page is expected to stay in memory. Low or quickly falling = memory pressure. |
| **Page verify** | How a database detects damaged pages. **CHECKSUM** (the default since SQL Server 2005) checks every page as it's read; TORN_PAGE_DETECTION and NONE catch little or nothing. |
| **Parameter sniffing** | A plan compiled for the parameter values of its first run, reused for very different values it doesn't suit. The usual sign is a query that's sometimes fast and sometimes slow: high variation, often with more than one plan. See [How-To Recipes](How-To-Recipes.md#find-a-query-thats-sometimes-slow-parameter-sniffing). |
| **Physical reads** | Pages read from disk because they weren't in memory |
| **Plan cache** | Where SQL Server keeps compiled plans for reuse. Cleared on restart and under memory pressure. |
| **Plan guide** | A hint attached to a query from outside, used to force a plan |
| **Plan regression** | A query getting a worse execution plan than before, and running slower |
| **Query hash** | A fingerprint that groups queries differing only in literal values |
| **Query Store** | A per-database feature (SQL Server 2016+, Azure SQL) that keeps query history and plans across restarts |
| **Query Store interval** | The time Query Store sums a plan's runtime statistics over (`INTERVAL_LENGTH_MINUTES`, 60 by default): one row per plan, interval and execution type. A period on the Query Store page counts whole intervals. |
| **Recovery model** | How a database keeps its transaction log. **SIMPLE**: the log is reused after each checkpoint, so the database can only be restored to its last full or differential backup. **FULL** (and **BULK_LOGGED**): the log is kept until a **log backup**, which allows a restore to any point in time; without log backups the log keeps growing. |
| **REORGANIZE / REBUILD** | Index maintenance. Reorganize is light and always online. Rebuild recreates the index and is thorough but heavier. |
| **Resource wait** | The part of a wait spent waiting for the resource itself (disk, lock, memory) |
| **RPO (recovery point objective)** | How much work, measured in time, you can afford to lose if a database has to be restored. Met by taking backups at least that often. See [Backups](Backups.md). |
| **sa** | SQL Server's built-in administrator login: always sysadmin and known to every attacker, so it's best disabled. Found by its SID, `0x01`, even when renamed. |
| **Server workspace** | From 0.57.0, a server you've opened: its name, health and Start/Stop at the top of the sidebar's server part, and its screens in groups under it. Opened from its tile on All servers, switched with the breadcrumb's server menu. See [The server workspace](Getting-Started.md#the-server-workspace). |
| **SID** | *Security identifier*: the number that ties a database user to its login, and identifies a Windows account whatever its name. |
| **Signal wait** | The part of a wait spent waiting for a CPU after the resource became available. A high share means CPU pressure. |
| **Snapshot** | In the top bar, a screen read once, when it was opened or last refreshed, rather than one that updates on its own (**Live**) or shows a time window (**History**). Its age is shown, in amber after 15 minutes. See [Live, Snapshot or History](Getting-Started.md#live-snapshot-or-history). |
| **SPID / session ID** | The number identifying a connection on the server |
| **SQL Server Agent** | The Windows service that runs scheduled **jobs** (backups, maintenance, ETL), each made of one or more **steps**. Not in Express or Azure SQL Database. See [Agent Jobs](Agent-Jobs.md). |
| **SSMS** | SQL Server Management Studio, Microsoft's free management tool |
| **Statistics** | Data-distribution summaries the optimizer uses to estimate row counts. **Stale** = many changes since the last update. |
| **sysadmin** | The fixed server role whose members can do anything on the server. See [Security Audit](Security-Audit.md#sysadmin-members). |
| **system_health** | An Extended Events session every SQL Server runs from the start. Among other things it records every deadlock, which is where the [Deadlocks](Deadlocks.md) page reads them. |
| **TempDB** | SQL Server's shared scratch database for temp tables, sorts, hashes and versioning |
| **Top bar** | The bar across the top of the screen, from 0.56.0: Back and Forward, the breadcrumb (*All servers › server › group › screen*), the database, the time range, whether the screen is Live, a Snapshot or History and how fresh, and Pause or Refresh. See [Getting Started](Getting-Started.md#2-find-your-way-around). |
| **TRUSTWORTHY** | A database setting that lets code in the database act outside it with its owner's permissions. With a sysadmin owner, db_owner in that database can become sysadmin. OFF except for msdb. |
| **VIEW ANY DEFINITION** | A server permission to see the definitions of every login, user, role and object, but no data. The [Security Audit](Security-Audit.md) needs it to list sysadmin members and orphaned users. |
| **VIEW SERVER STATE** | The permission needed to read server-level DMVs. On Azure SQL Database the equivalent is `VIEW DATABASE STATE`. |
| **Wait / wait type** | A reason a task couldn't continue, and how long it waited. See [Wait Statistics](Wait-Statistics.md). |
| **Wait category** | Query Store's grouping of wait types per query (SQL Server 2017+), e.g. *Lock* for `LCK_M_%` or *Buffer IO* for `PAGEIOLATCH_%`. See [Query Store → Waits](Query-Performance.md#waits). |
| **Webhook** | A URL that posts whatever is sent to it into one Slack or Microsoft Teams channel. Anyone who has it can post there, so SqlVitals stores it encrypted and never logs it. See [Chat Notifications](Chat-Notifications.md). |
