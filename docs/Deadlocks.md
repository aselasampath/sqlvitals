# Deadlocks

> **Where:** sidebar → 💥 Deadlocks · **Use it when:** users get error 1205 (*"Transaction … was deadlocked … and has been chosen as the deadlock victim"*), or the same deadlock keeps coming back · **Needs:** `VIEW SERVER STATE` (`VIEW SERVER PERFORMANCE STATE` on SQL Server 2022 and later) · **New in 0.38.0**

A **deadlock** is two or more sessions each holding a lock the other needs, so none of them can go on. SQL Server's deadlock monitor finds the circle within seconds, picks one session as the **victim**, rolls its transaction back with error 1205 and lets the others finish.

Every SQL Server already records each deadlock in **system_health**, an Extended Events session that is on from the start. This page reads those reports. There is nothing to set up, and SqlVitals creates nothing on the server.

---

## How to use it

1. Open **💥 Deadlocks**. It reads the reports system_health still holds, newest first. The line under the toolbar says where they came from and how far back they go.
2. **Show** narrows the list to the last hour, 24 hours, 7 days or 30 days. *Everything system_health has* is the default. **↻ Read again** (or the sidebar **Refresh**) reads the server again for deadlocks since you opened the page.
3. Each row is one deadlock:

   | Column | Meaning |
   |---|---|
   | **Time** | When the deadlock monitor found it, on this PC's clock |
   | **Victim** (red) | The session that was rolled back: SPID, login, and the procedure or statement it was running |
   | **Other participants** | The sessions that went on |
   | **Objects** | The tables (and indexes) whose locks they fought over |
   | **Database** | The participants' database |
   | **Seen** | How many deadlocks in the range have the same pattern. **×2** or more is shown in amber: it keeps coming back |
   | **Victim statement** | The first line of the statement that failed |

4. **Select a deadlock** to see it in full below the list:
   - **Participants**, the victim first: login, host, application, database, the procedure or statement, what it was **Waiting for** and what it was **Holding** (lock mode on object), how long it had waited, isolation level and open transactions (`trancount`).
   - **Resources**: each lock, with the sessions that **held** it and the ones that **wanted** it.
   - **Select a participant** to see its full statement and its **input buffer** (the batch the application sent).
5. **Save as .xdl…** saves the deadlock graph. Open the file in SSMS (**File → Open → File…**) to see the processes and resources drawn as a graph. **Copy XML** copies the same report.
6. The **Recurring** tab groups deadlocks that look alike (the same objects fought over by the same code), the most frequent first, with when each pattern was first and last seen. Double-click a row, or press **Open newest**, to open its latest deadlock.

The grids have the usual filter box and right-click menu (copy, export to CSV). See [Working with Grids](Working-with-Grids.md).

---

## Reading it

Look at **Waiting for** and **Holding** side by side. In the classic deadlock, one session holds `X on Orders` and wants `U on OrderLines`, and the other holds `X on OrderLines` and wants `U on Orders`: they touched the same two tables **in the opposite order**.

Common causes and fixes:

| What you see | Usually means | Try |
|---|---|---|
| Two procedures updating the same tables in a different order | Lock order | Change one so both touch the tables in the same order |
| `U` or `S` wanted on a key, `X` held on the same table by the other | A scan, not a seek, took more locks than needed | An index that supports the `WHERE` clause (see [Index Health](Index-and-Statistics-Maintenance.md)) |
| `trancount` 2 or more, and a short statement | The application wrapped several calls in one long transaction | Shorter transactions; commit sooner |
| `serializable` isolation level | Range locks (often from `TransactionScope` in .NET, which defaults to it) | `read committed`, or snapshot isolation where the application allows it |
| **Parallel exchange** as the resource, one SPID for every participant | A parallel query deadlocked with itself | Usually resolves itself; lower `MAXDOP` for the query or add an index so it doesn't go parallel |
| The same pattern many times a day on the **Recurring** tab | A design problem, not bad luck | Fix that one first |

A deadlock victim's transaction is rolled back, so the application should **retry** error 1205. Retrying hides the symptom; the patterns above remove the cause.

A circle of sessions shown on the [Processes](Live-Monitoring.md#processes-blocking-chains) page is a deadlock the monitor hasn't resolved yet. Seconds later it appears here.

---

## How far back it goes

system_health writes to **event files** (`system_health*.xel` in SQL Server's log folder) and keeps a few of them, removing the oldest as they fill. How much time that covers depends on how busy the server is: days on a quiet server, hours on a very busy one. It also records other events, so a server that logs many errors reaches less far back. The files survive a restart.

When the event files can't be read (for example, the log folder moved or the login can't reach it), the page reads system_health's **ring buffer** instead and says so in amber. The ring buffer holds only the most recent events and is emptied when SQL Server restarts.

At most the newest 1,000 deadlocks are shown; the status line says when there were more.

To keep deadlocks longer, save the ones you care about as `.xdl`, or ask your DBA to make system_health's files larger (`ALTER EVENT SESSION system_health ON SERVER … max_file_size / max_rollover_files`).

---

## When it shows nothing

| Message | Why | What to do |
|---|---|---|
| *system_health holds no deadlock reports* | No deadlock in the time its files cover | Nothing. That's good news |
| *No deadlocks in the last …* | There are some, but older | Pick a longer range |
| *This login can't read Extended Events sessions* | `VIEW SERVER STATE` is missing | `GRANT VIEW SERVER STATE TO [login];` (`VIEW SERVER PERFORMANCE STATE` on 2022+) |
| *The system_health … session isn't running* | Someone stopped it | `ALTER EVENT SESSION system_health ON SERVER STATE = START;` |
| *Azure SQL Database has no system_health session* | Azure SQL Database keeps deadlocks elsewhere (in `master`) | Not supported on this page yet. Managed Instance and SQL Server are |

---

### Under the hood

- `DeadlockRepository.GetDeadlockHistoryAsync` finds system_health's targets in `sys.dm_xe_sessions` / `sys.dm_xe_session_targets`. The event file target's `target_data` names the current file; the folder and name become a wildcard (`…\Log\system_health*.xel`) so every rollover file is read with `sys.fn_xe_file_target_read_file`, **filtered on the server to `object_name = 'xml_deadlock_report'`**. If that fails, the `ring_buffer` target's XML is read instead. The file read has a 2-minute timeout.
- The XML is shredded in C#, not on the server: `Engine/Deadlocks/DeadlockReportParser` reads processes (`process-list`), victims (`victim-list`, or the `victim` attribute of the SQL Server 2008 format), resources (`resource-list`: key, page, RID, object, metadata, application locks, parallel exchanges…) and who owned or waited for each. The statement is the first frame of the execution stack with text; `adhoc` / `unknown` placeholders fall back to the input buffer.
- The `.xdl` is the report's `<deadlock>` element, unchanged: what SSMS expects.
- `Engine/Deadlocks/DeadlockPatterns` groups reports by the objects (without the index) and the code: procedure names, or statements with literals and spacing removed, so the same ad hoc statement with other values counts once.
- It runs only when the page is opened or refreshed; there's no timer. See [Server Impact and Permissions](Server-Impact-and-Permissions.md).
