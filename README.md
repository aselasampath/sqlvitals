<p align="center">
  <img src="SqlVitals/Branding/sqlvitals-256.png" alt="SqlVitals logo" width="128" height="128">
</p>

<h1 align="center">SqlVitals</h1>

<p align="center">
  <strong>Check the pulse of your SQL Server or Azure SQL database, spot trouble early and fix it with confidence.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?logo=windows" alt="Platform: Windows 10 | 11">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8.0">
  <img src="https://img.shields.io/badge/SQL%20Server%20%7C%20Azure%20SQL-supported-CC2927?logo=microsoftsqlserver" alt="SQL Server and Azure SQL">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="License: MIT"></a>
  <a href="https://github.com/aselasampath/sqlvitals/actions/workflows/pr-setup.yml"><img src="https://github.com/aselasampath/sqlvitals/actions/workflows/pr-setup.yml/badge.svg" alt="PR build"></a>
  <a href="https://github.com/aselasampath/sqlvitals/releases/latest"><img src="https://img.shields.io/github/v/release/aselasampath/sqlvitals" alt="Latest release"></a>
</p>

<p align="center">
  <a href="https://github.com/aselasampath/sqlvitals/releases/latest"><strong>Download</strong></a> ·
  <a href="https://github.com/aselasampath/sqlvitals/wiki"><strong>Documentation</strong></a> ·
  <a href="https://github.com/aselasampath/sqlvitals/wiki/Getting-Started"><strong>Getting Started</strong></a> ·
  <a href="https://github.com/aselasampath/sqlvitals/issues"><strong>Report an issue</strong></a>
</p>

**SqlVitals** is a free, open-source Windows desktop app that shows what your SQL Server or Azure SQL database is
doing right now, and what it was doing while you weren't watching. It reads the server's own dynamic management
views directly: no agent on the server, no monitoring database to look after, no web service in between. Add a
connection, and waits, blocking, slow queries and index problems are on screen in seconds.

Built for DBAs and developers who need a clear answer to *"why is the database slow?"* without setting up a
monitoring platform first.

## Features

- 🩺 **Live health.** A 0–100 health score and a 🟢 🟠 🔴 dot for every server, live metrics, active waits and
  blocking chains with the head blocker named.
- 🔔 **Watches in the background.** Alerts with start, end and worst value, and Windows notifications, even with the
  window closed to the notification area.
- 🕰️ **Remembers.** A local history of metrics, waits and top queries, compared with the same time last week.
- 🧯 **The DBA checklist.** Deadlock history, failed and long-running SQL Agent jobs, backups against your RPO, file
  I/O latency, and server settings checked against best practice.
- 📉 **Query performance.** The busiest queries, Query Store, query regressions with before and after plans,
  graphical execution plans, implicit conversions and a live stored-procedure trace.
- 🛠️ **Fix, not just find.** Scripts for missing and unused indexes, fragmentation, stale statistics and
  misconfigured settings. You review and run them; SqlVitals never does.
- 📤 **Easy to share.** Copy or export any grid to CSV, or export a report ready to paste into an AI assistant.

See the [feature map](https://github.com/aselasampath/sqlvitals/wiki#feature-map) in the wiki for every screen.

## Get started

1. Download `SqlVitals-Setup-<version>.exe` from the [latest release](https://github.com/aselasampath/sqlvitals/releases/latest)
   and run it. Windows 10 or 11; no admin rights needed, and the .NET runtime is included.
   Rather not run an installer? Download `SqlVitals-<version>-win-x64-portable.zip`, unzip it and run `SqlVitals.Desktop.exe`.
2. Ask for read access to the server's diagnostic views for the login you'll use:

   ```sql
   GRANT VIEW SERVER STATE TO [your_login];     -- SQL Server and Azure SQL Managed Instance
   GRANT VIEW DATABASE STATE TO [your_user];    -- Azure SQL Database, in the user database
   ```

   That covers almost every screen. Agent Jobs and Backups also read msdb, and the SP Trace's per-call mode needs
   one more grant: see [Server Impact and Permissions](https://github.com/aselasampath/sqlvitals/wiki/Server-Impact-and-Permissions).
3. Add the connection in **Settings** and click **Save & Connect**. [Getting Started](https://github.com/aselasampath/sqlvitals/wiki/Getting-Started)
   walks through the first look.

## Safe on production servers

- **Read-only.** SqlVitals never changes a server. The scripts it writes are for you to review and run.
- **Light.** Reads use `READ UNCOMMITTED` with a timeout. In the background it runs one light query per server
  every 10 seconds and a snapshot every 5 minutes; heavy screens run only when you open them.
- **Private.** Saved connections are encrypted with Windows DPAPI, and history stays on your PC. See
  [Security and Privacy](https://github.com/aselasampath/sqlvitals/wiki/Security-and-Privacy).

## How it compares

| | SqlVitals | SSMS | Azure portal / Azure Monitor |
|---|---|---|---|
| On-premises SQL Server and Azure SQL in one view | ✅ | ✅ | Azure SQL. On-premises needs Azure Arc |
| Several servers watched continuously, with a health score each | ✅ | ❌ Activity Monitor: one server, while open | ⚠️ Per-resource metrics and alert rules you configure |
| History, compared with last week | ✅ Local, retention you choose | ⚠️ Query Store reports, per database | ⚠️ Platform metrics; deeper data needs Log Analytics (billed per GB) |
| Alerts and desktop notifications | ✅ Built in | ⚠️ SQL Agent alerts, set up per server | ✅ Alert rules and action groups, set up per resource |
| Blocking chains, deadlocks, Agent jobs, backups, file I/O, configuration | ✅ One place | ⚠️ Separate reports and monitors, one server at a time | ⚠️ Partly, Azure SQL only |
| Query regressions with before and after plans | ✅ Query Store or its own history | ⚠️ Regressed Queries report, Query Store only | ⚠️ Query Performance Insight, Azure SQL Database only |
| Fix scripts for indexes, statistics and settings | ✅ Review, then run yourself | ⚠️ Missing-index hint in a plan | ⚠️ Automatic tuning, Azure SQL only |
| Nothing to install on the server, no workspace, no cost | ✅ | ✅ | ⚠️ Log Analytics and some features are billed |

*A fair summary of the built-in tools as usually used; SSMS and Azure change often, and both do far more than this
table shows.*

**What SqlVitals is not:**

- **Not an administration tool.** Keep SSMS or Azure Data Studio for creating objects, security, taking backups and
  running the scripts SqlVitals generates.
- **Not a team monitoring platform.** History and alerts live on the PC running SqlVitals and are collected while it
  runs. For 24/7 monitoring shared by a team, use a server-based product.
- **Windows only.** It's a WPF desktop app.

## Documentation

Everything else is in the **[wiki](https://github.com/aselasampath/sqlvitals/wiki)**:

- **Using it:** [Installation](https://github.com/aselasampath/sqlvitals/wiki/Installation) ·
  [Getting Started](https://github.com/aselasampath/sqlvitals/wiki/Getting-Started) ·
  [How-To Recipes](https://github.com/aselasampath/sqlvitals/wiki/How-To-Recipes) ·
  [Troubleshooting and FAQ](https://github.com/aselasampath/sqlvitals/wiki/Troubleshooting-and-FAQ) ·
  [Glossary](https://github.com/aselasampath/sqlvitals/wiki/Glossary)
- **Before a DBA approves it:** [Server Impact and Permissions](https://github.com/aselasampath/sqlvitals/wiki/Server-Impact-and-Permissions) ·
  [Security and Privacy](https://github.com/aselasampath/sqlvitals/wiki/Security-and-Privacy)
- **Developers:** [Architecture](https://github.com/aselasampath/sqlvitals/wiki/Architecture) ·
  [Building and Contributing](https://github.com/aselasampath/sqlvitals/wiki/Building-and-Contributing)

## Build from source

Needs Windows 10/11 and the [.NET SDK](https://dotnet.microsoft.com/download) 9.0.200 or later (the projects target
.NET 8; the `.slnx` solution needs the newer SDK).

```powershell
git clone https://github.com/aselasampath/sqlvitals.git
cd sqlvitals
dotnet build SqlVitalsDashboard.slnx
dotnet test
dotnet run --project SqlVitals\Desktop
```

The installer, CI, releases and how to add a screen are in
[Building and Contributing](https://github.com/aselasampath/sqlvitals/wiki/Building-and-Contributing).

## Contributing

Issues and pull requests are welcome. Work is tracked in [issues](https://github.com/aselasampath/sqlvitals/issues),
one branch and pull request per issue; see the
[contribution workflow](https://github.com/aselasampath/sqlvitals/wiki/Building-and-Contributing#contribution-workflow).

## License

[MIT](LICENSE)
