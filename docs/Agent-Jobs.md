# Agent Jobs

> **Where:** sidebar → 🕒 Agent Jobs · **Use it when:** a nightly backup, index maintenance or ETL job may have failed, or is taking far longer than usual · **Needs:** read access to SQL Agent's tables in `msdb` (sysadmin, or `SELECT` on them: see [Server Impact and Permissions](Server-Impact-and-Permissions.md#sql-agent-jobs)) · **Not on:** Azure SQL Database, which has no SQL Server Agent · **New in 0.39.0**

**SQL Server Agent** runs scheduled **jobs**: backups, index and statistics maintenance, integrity checks, ETL loads. When one fails, or hangs, nothing on the application side notices until data goes missing or a disk fills up. This page lists every job with how its last run went and whether one is running for longer than it usually takes.

Everything is read from `msdb`, where Agent keeps its jobs and their history. SqlVitals changes nothing and never starts or stops a job.

---

## How to use it

1. Open **🕒 Agent Jobs**. The jobs that need you come first: **running long** (amber), then **failed** (red), then running, then the rest by name.
2. The line under the toolbar counts them: *42 jobs · 3 failed · 2 running, 1 longer than usual · 5 disabled*. An amber line above the list means **SQL Server Agent itself isn't running**, so no job starts on its schedule.
3. **Show** narrows the list: *Needs attention: failed or running long*, *Running now*, *Last run failed* or *Enabled jobs*. **↻ Read again** (or **Refresh** in the top bar, **F5**) reads msdb again. Tick **Every 30 s** to watch a long job while the page is open.
4. Each row is one job:

   | Column | Meaning |
   |---|---|
   | **Status** | **Running long** (amber): running now for more than a minute past its average successful run. **Failed** (red): its last run failed. **Running**, **Succeeded**, **Canceled**, **Never run** (no run left in the history). Hover for the details |
   | **Job** | The job's name. Disabled jobs are greyed |
   | **Last run** | When the last finished run started |
   | **Last outcome** | How the last finished run ended. **Failed** stays red while the job runs again |
   | **Duration** | How long it's been running so far, or how long the last run took (h:mm:ss). Amber when running long |
   | **Average** | Average duration of its **successful** runs in the history |
   | **vs avg** | Duration ÷ average: **×2.0** took twice as long as usual |
   | **Next run** | When its enabled schedules run it next. *(due)*: the time has passed and it hasn't started. Otherwise *Job disabled*, *Not scheduled*, *When Agent starts*, *When the CPU is idle* or *No next run* (for example a one-time schedule that has run) |
   | **Enabled** | Whether the job is enabled |
   | **Failed runs** | Failed runs out of the runs msdb still keeps: **2 of 30**. More than one points to a job that fails often, not once |
   | **Category**, **Owner** | As in SSMS. *(no such login)*: the owner's login no longer exists, which can stop the job from starting |
   | **Last message** | Agent's message for the last run, e.g. *The job failed. … The last step to run was step 2 (Load facts).* |

5. **Select a job** to see its runs below the list, newest first:
   - Each **run** with its start, outcome and duration. For a **failed** run the message is the **failing step and its error**, e.g. *Step 2 (Load facts): Violation of PRIMARY KEY constraint …*. A run in progress shows as **Running**.
   - **Select a run** to see its **steps**: each step's outcome, start, duration and message. The failing step is selected for you, and its full message is shown on the right.

The grids have the usual filter box and right-click menu (copy, export to CSV). See [Working with Grids](Working-with-Grids.md).

---

## Reading it

| What you see | Usually means | Try |
|---|---|---|
| **Failed**, and the failing step's message names a SQL error | The step's T-SQL failed: a constraint, a missing object, a timeout | Fix the code or data the step works on; run the step's command in SSMS to reproduce |
| **Failed** with *Unable to determine if the owner … has server access*, or Owner *(no such login)* | The job's owner login was dropped or can't be checked (often a domain account) | Change the owner in SSMS (Job Properties → Owner), commonly to `sa` or a dedicated login |
| **Failed** for a backup job with *Operating system error 112* / *3* | The backup disk is full or the path is gone | Free space or fix the path; the next run will tell |
| **Running long** at ×3 or more | Blocked, waiting for a lock, or much more data than usual | Open [Processes](Live-Monitoring.md#processes-blocking-chains): the job's session (program *SQLAgent - TSQL JobStep*) may be blocked |
| **Failed runs** high but the last run **Succeeded** | A flaky job: timeouts, deadlocks, a network share not always there | Look at the failed runs' messages; a retry on the step may be enough, the cause is better |
| **Next run** *(due)* on many jobs | Agent is behind or stopped | See the amber line at the top; start SQL Server Agent |
| **Never run** on an enabled, scheduled job | New, or its history was purged | Nothing, unless it should have run by now |

A job still **running** hours later with no progress in its steps is often stuck, not slow. **Duration** against **Average** says which.

---

## What "longer than its average" means

The average is of the job's **successful** runs that `msdb.dbo.sysjobhistory` still keeps. Failed runs are left out: they often stop early, and would pull the average down. A running job counts as **running long** when it has been running for **more than a minute past** that average, so a few seconds either side of normal don't turn it amber. A job with no successful run in the history has no average and is never marked running long.

msdb trims its job history: by default to **1,000 rows** in all and **100 rows per job** (SQL Server Agent Properties → History), counting each step as a row. A job with many steps therefore keeps fewer runs, and the average and *Failed runs* cover only those.

---

## Times

All times are the **server's local time**: msdb stores them that way and the schedules are written in it, as SSMS shows them. The status line says what time it was on the server when the jobs were read.

---

## When it shows nothing

| Message | Why | What to do |
|---|---|---|
| *There are no SQL Agent jobs on this server* | None have been created | Nothing |
| *This login can't read SQL Server Agent's tables in msdb* | The login isn't sysadmin and has no read access to msdb's job tables | Grant `SELECT` on the job tables to its msdb user (script in [Server Impact and Permissions](Server-Impact-and-Permissions.md#sql-agent-jobs)) |
| Amber: *SQL Server Agent isn't running* | The Agent service is stopped | Start it in SQL Server Configuration Manager or SSMS Object Explorer. The jobs are still listed |
| Amber: *SQL Server Express has no SQL Server Agent* | Express can't run Agent | Windows Task Scheduler running `sqlcmd` is the usual stand-in |
| *Azure SQL Database has no SQL Server Agent* | Azure SQL Database has no Agent at all; the **🕒 Agent Jobs** button is disabled (dimmed and marked **N/A**) for such a connection | Use Elastic Jobs or Azure Automation there. **Azure SQL Managed Instance** has Agent and works here |

Whether Agent is running is read from `sys.dm_server_services`, which needs `VIEW SERVER STATE`. Without it, the `Agent XPs` server option (Agent turns it on as it starts and off as it stops) decides; if even that can't be read, the status line says *state unknown* and the jobs are shown as msdb has them.

---

### Under the hood

- `AgentJobRepository.GetAgentJobsAsync` first checks `SERVERPROPERTY('EngineEdition')` (5 = Azure SQL Database: stop there with a message). It then reads Agent's state (`sys.dm_server_services`, else the `Agent XPs` option), and one row per job from `msdb.dbo.sysjobs` with:
  - the newest **job outcome** row (`step_id = 0`) in `sysjobhistory`: `run_status`, `run_date` / `run_time`, `run_duration` (hhmmss), `message`;
  - counts of runs, failed runs and the **average `run_duration` of successful runs**, computed on the server;
  - `sysjobactivity` for the **current Agent session** (`MAX(session_id)` in `syssessions`): a `start_execution_date` with no `stop_execution_date` is a run in progress; `next_scheduled_run_date`;
  - the earliest `next_run_date` / `next_run_time` of its **enabled** schedules in `sysjobschedules` + `sysschedules`, and whether any start with Agent (`freq_type` 64) or when idle (128).
- `Engine/AgentJobs/AgentJob.From` (pure, unit tested) turns that into the status. Next run is the earliest of `sysjobactivity` and `sysjobschedules` still ahead, because Agent refreshes `sysjobschedules` only every 20 minutes. A run left "started" while Agent is stopped was cut short, and doesn't count as running.
- Selecting a job reads up to 2,000 of its `sysjobhistory` rows. `Engine/AgentJobs/AgentJobHistory.GroupRuns` groups them into runs: Agent writes each step's row as it ends and the job outcome (step 0) last, so a run's steps are the rows before its outcome row. Steps that started before the run they precede are shown as a run cut short.
- Missing msdb permission (errors 229 and 916) becomes the message above, not an error box.
- It runs when the page is opened or refreshed, and every 30 s only when **Every 30 s** is ticked. See [Server Impact and Permissions](Server-Impact-and-Permissions.md).
