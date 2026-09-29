# Troubleshooting and FAQ

## Connecting

**"Save" fails with a connection error.**
SqlVitals checks every connection before saving it. Check, in order:
1. The **server name**: include the instance (`host\INSTANCE`) or port (`host,1433`).
2. **Firewall**: can this PC reach the SQL port?
3. **Encryption**: an on-premises server with a self-signed certificate needs **Trust server certificate** ticked. An old server may need **Encrypt: Optional**.
4. **Credentials**: try the same login in SSMS.

**A screen shows a permission error.**
Your login lacks `VIEW SERVER STATE` (or `VIEW DATABASE STATE` on Azure SQL Database). See [Installation → Ask your DBA](Installation.md#ask-your-dba-for-permission).

**I'm asked for my password every time I start the app.**
**Remember password** is unticked for that connection. That's deliberate: passwords then never reach disk. Tick it if you prefer.

**My Entra MFA connection's health dot stays ◯.**
MFA connections can be monitored only after you've switched to them once in the session, so you never get unexpected sign-in windows: until then their **▶** is greyed out. Select it once after starting the app.

**A server's health score is 0.**
The last collection failed: the server can't be reached, the login failed or a permission is missing. Click the score to see the error. SqlVitals keeps retrying, less and less often (up to every 5 minutes).

**The score is 80 but shows red.**
The colour is the health dot's: one check is past its critical threshold. A single critical check costs 12 to 20 points, so the number can stay high. Click the score to see which. See [Health score](Health-Dot-and-Background-Monitoring.md#health-score).

**My saved connections are gone, and there's a `settings.unreadable-….dat` file.**
SqlVitals couldn't read `%AppData%\SqlVitals\settings.dat`: it was damaged, or it came from another Windows user or PC (it's encrypted for the user who saved it). It kept a copy before starting afresh. If your Windows profile was restored, rename the copy back to `settings.dat` while SqlVitals is closed; otherwise add the connections again. Settings that aren't encrypted, like thresholds, are kept when only the connections couldn't be read.

**Saving a setting says SqlVitals "couldn't read its settings file just now".**
Another program, usually a sync or antivirus tool, had `settings.dat` open. SqlVitals won't save over a file it couldn't read, so nothing was lost. Try again in a moment.

**CPU showed 0 % on Azure SQL Database.**
Before 0.36.0 CPU was read from records Azure SQL Database doesn't have. From 0.36.0 it's the portal's *CPU percentage* (`sys.dm_db_resource_stats`), per 15 seconds, so it can jump between 0 and a high figure where the portal's one-minute average looks smoother.

## Alerts

**The Alerts page has no alerts, but the health dot went 🟠.**
An alert needs the indicator past its threshold for **3 samples in a row** (30 seconds at the default interval), so a brief spike changes the dot without raising one. Lower **Settings → Alerts → Raise an alert after** to alert sooner. Alerts are also raised only for monitored, saved connections: from 0.46.0 nothing is monitored until you press **Start** (Live Metrics, or ▶ in the sidebar selector) or turn on **Settings → Start monitoring automatically**. The Alerts page says when monitoring is off. See [Alerts](Alerts.md).

**An alert ended with "Monitoring stopped".**
Nothing was watching any more: collection was stopped on Live Metrics, the connection was edited or removed, or SqlVitals exited (or was killed). If the problem is still there, a new alert starts once monitoring resumes. See [Alerts → When monitoring stops](Alerts.md#when-monitoring-stops).

## Notifications and the notification area

**I closed the window but SqlVitals is still running.**
That's the default from 0.35.0: closing or minimizing hides it to the notification area so monitoring and alerts carry on. Right-click the SqlVitals icon (beside the clock; it may be under **^**) → **Exit** to quit, or turn it off in **Settings → Notification Area**. See [Notifications and Tray](Notifications-and-Tray.md).

**Where did the window go? Starting SqlVitals again does nothing.**
It's running in the notification area, and starting it again brings that copy to the front. If no window appears, click the SqlVitals icon in the notification area.

**No notification appeared when an alert started.**
Check, in order:
1. The alert is on the **Alerts** page. No alert means no notification: see *The Alerts page has no alerts* above.
2. The connection is ticked in **Settings → Alerts → Windows notifications**.
3. Windows **Do Not Disturb** / **Focus** is off. Otherwise the notification waits in the notification centre.
4. **Windows Settings → System → Notifications → SqlVitals** is on.

An alert getting worse or ending doesn't notify; only starting or turning critical does.

**Setup says SqlVitals is running, but I closed it.**
Closing only hides it. Right-click its icon in the notification area → **Exit**, then click **Check again** (or **Retry**) in Setup.

## Screens and data

**A screen says a query timed out.**
Large servers can take longer than 30 seconds on heavy screens (Index Health, Implicit Conversions, Plan Cache Health). Raise **Settings → Command Timeout**.

**Query Store says "Query Store is off for *database*".**
Query Store is off for the connected database. Each tab shows the statement to copy, `ALTER DATABASE [YourDb] SET QUERY_STORE = ON;`; run it yourself or ask your DBA. [Query Regressions](Query-Regressions.md) falls back to monitoring history in the meantime.

**The Waits tab on Query Store is empty.**
- *"This version of SQL Server doesn't record waits in Query Store"*: SQL Server 2016 has no per-query waits; they came with 2017.
- *"Wait stats capture is off"*: copy the `ALTER DATABASE … SET QUERY_STORE (WAIT_STATS_CAPTURE_MODE = ON);` it shows. Waits are recorded from then on, not before.
- *"No waits recorded"*: nothing waited in the period, or Query Store was read-only (see the settings checks at the top of the page).

**A Query Store tab times out.**
Its reads have a 2-minute timeout, as on Query Regressions. A shorter period (e.g. *Last 7 days* instead of *30*) reads fewer runtime-stats rows.

**DB Storage shows no Used or Free space for some databases.**
Used space is read inside each database, so it's shown only for databases your login can open. Sizes and settings are shown for every database.

**Unused Indexes lists indexes I know are used.**
Usage counters reset when SQL Server restarts (and on Azure SQL failovers). "Unused" means *since the last restart*. Don't drop an index without checking how long the server has been up.

**Top Waits hasn't changed even though last night was terrible.**
Top Waits accumulates since the last restart, so one night barely moves months of totals. Use **Wait Trend** with a **24h** or **Custom** range instead.

**Some sidebar items are dimmed and marked N/A.**
The connection is to an Azure SQL Database, which has no TempDB files, SQL Server Agent, msdb backup history or server-level security settings to show. Hover an item for the reason. See [Server Impact and Permissions → Compatibility](Server-Impact-and-Permissions.md#compatibility).

**The history ranges (1h / 24h / 7d) are greyed out.**
You're on the ad-hoc connection, which isn't recorded. Save the connection in Settings.

**A history chart is empty or has gaps.**
History is only recorded while SqlVitals is running and the connection is **monitored** (from 0.46.0 nothing is, until you press Start or turn on **Settings → Start monitoring automatically**). Gaps are periods when it wasn't: **Settings → Activity Log** says when monitoring stopped, and why (see [How-To: find out why there's a gap](How-To-Recipes.md#find-out-why-theres-a-gap-in-the-history)). Perfmon history exists only from version 0.30.

**Compare to baseline shows no dashed line.**
Nothing was recorded at the same time last week. Keep history for at least 14 days (**Settings → Monitoring History**).

**The execution plan window shows no diagram.**
*"No execution plan available — the plan may have been evicted from cache"* means SQL Server has already evicted the plan: run the query again, or find it in Query Store. If the XML can't be read, the parser's message is shown and the **XML** tab still has what was received. See [Execution Plans](Execution-Plans.md#when-theres-no-diagram).

**The Cardinality Misestimates or Skewed Parallelism tab is empty.**
These *(live)* tabs only show queries running at that moment, and need lightweight query profiling (default from SQL Server 2019 and in Azure SQL). Refresh while the problem is happening.

**A page stopped updating, and the status bar shows an error.**
When automatic refresh fails, SqlVitals shows the error in the status bar instead of popping up a dialog every few seconds. Fix the cause (often connectivity), then press **↻ Refresh**.

## SP Trace

**The badge says "DMV fallback".**
Your login can't create Extended Events sessions. You still get per-procedure totals. For per-call rows, see [SP Trace → Permissions](SP-Trace.md#permissions).

**Is a trace session left running on my server?**
Not after you stop it. It's dropped on Stop, when you leave the page, and when the app exits. Closing the window only hides it to the notification area, and a running trace keeps running until you stop it or choose **Exit**. A session left by a crash is removed at the next Start. To check or remove it by hand, see [SP Trace → Under the hood](SP-Trace.md#under-the-hood).

## General

**Does SqlVitals change anything on my server?**
No. It only reads diagnostic views. The one exception is SP Trace's temporary event session. Generated scripts are never run. See [Server Impact and Permissions](Server-Impact-and-Permissions.md).

**Does it send my data anywhere?**
No. There's no telemetry. Everything stays on your PC. See [Security and Privacy](Security-and-Privacy.md).

**The dark theme resets when I restart.**
Known limitation: the theme choice isn't saved yet.

**How do I report a bug?**
**Settings → Diagnostics → Open log folder**, take the latest `SqlVitals-yyyyMMdd.log` (passwords are already removed), and attach it to a [new issue](https://github.com/aselasampath/sqlvitals/issues) with what you did and what you expected.
