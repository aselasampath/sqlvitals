# History, Time Ranges and Baselines

SqlVitals keeps a **local history** of every server it monitors, so you can answer *"what happened at 2 AM?"* the next morning, even if the app wasn't on screen.

> **Where:** the range picker in the [top bar](Getting-Started.md#live-snapshot-or-history) on a server's [Overview](Your-Servers.md#charts) (from 0.59.0, without the baseline), 💓 Live charts, 📈 Wait Trend and ⏱️ Perfmon (in each page's toolbar before 0.56.0) · **Needs:** a saved connection that was [monitored](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring) at the time

## Look back in time

The range picker has five choices:

| Choice | Shows |
|---|---|
| **Live** | Data collected as you watch (the default) |
| **1h** | The last hour |
| **24h** | The last 24 hours |
| **7d** | The last 7 days |
| **Custom…** | A **From** and **To** date and time you choose (on this PC's clock). Press **Show**. |

What each page shows for past ranges:

| Page | Shows | Detail |
|---|---|---|
| **Live charts** | Every chart | One point per sample (10 s by default) on 1h |
| **Overview** | Every chart, and the health score | The same; the health score is the lowest in each slice of time |
| **Wait Trend** | Waits per type, any of the four metrics | One point per snapshot (5 min by default) |
| **Perfmon** | The counters you tick | One point per snapshot (recorded from version 0.30) |

**Refresh** (or **F5**) re-reads the range, so *1h* moves up to "now".

From 0.56.0 the picker is in the top bar, beside the page's badge. On **Live** the badge says **Live** with how often the page updates and when it last did; pick a past range and it says **History**, with **Data to** the end of the newest data in the range, so a range that stops early because monitoring was off shows it. On Live Metrics, **Sample every** is beside the picker too. In a narrow window the picker moves to a row of its own under the bar, so the server's name stays in view.

![The top bar on a history page: the database, the History badge, the window, what it is compared with, and where the data ends](images/top-bar/history.png)

### Reading history charts

- **Long ranges are averaged** so a chart has at most 600 points: about 10 s per point on 1h, 5 min on 24h and 30 min on 7d. The status line says how many points there are and how far apart.
- **Gaps stay gaps.** When the app was closed or monitoring stopped, the line breaks instead of drawing a misleading straight line across.
- **Live charts and the Overview keep collecting** while you look at a past range, so nothing goes unrecorded. **Wait Trend and Perfmon** stop their own live polling; press **Live**, then **Resume (▶)** in the top bar, to resume.

## Compare to a baseline

Tick **Compare to baseline** (part of the range picker) to overlay **the same time last week** as **dashed, faded lines**. It answers *"is this normal?"*: a CPU of 70 % is worrying if last Tuesday at the same hour was 20 %, and normal if it was 70 % too.

- Each line gets a dashed twin in the same colour, called *"… · last week"* in the legend.
- On Live charts' stacked waits chart, the baseline is one dashed **Total waits · last week** line, counting only the wait categories that are ticked.
- **Same wall-clock time:** 09:00 today is compared with 09:00 last week, even across a daylight-saving change.
- Works with **Live** and with any past range.
- Where nothing was recorded last week, there's no line, and the status line says so.

Baselines need a week of history. See [Settings → Monitoring & history](Settings.md#monitoring--history) to keep enough.

## What is recorded, and how often

| What | How often | Examples |
|---|---|---|
| **Live Metrics samples** | Every Live Metrics interval (10 s by default) | CPU, waits per category, batch requests, compilations, transactions, physical I/O, page life expectancy, memory grants pending, buffer cache hit ratio |
| **Detail snapshots** | Every **5 minutes** by default (1, 5, 15 or 30 in Settings) | Waits per type, file I/O per file, top 20 queries by CPU, memory, Perfmon counters |

History is kept **14 days** by default (7, 14, 30 or 90 in Settings) and deleted automatically after that. The **Query Regressions** screen can also use it. See [Query Regressions](Query-Regressions.md).

---

### Under the hood

- **Storage:** a SQLite file at `%LocalAppData%\SqlVitals\history.db`. Schema, tiers, retention and how to query it yourself are in [Monitoring History Database](Monitoring-History-Database.md).
- **Reads** happen on a background thread through a separate, short-lived read-only connection to the file (`HistoryReader`), so they never block the UI or the writer.
- **Bucketing** (`HistoryRange`) picks a bucket size so the range has at most 600 points and averages samples within each bucket. **Gaps** (`HistoryGaps`) are detected from the expected interval, and the chart gets a null point so the line breaks.
- **Clocks:** Live Metrics plots past ranges on the **server's** clock, like its live samples (`server_time`). Wait Trend and Perfmon use **this PC's** clock (`captured_utc`) for both.
- **Baseline** (`HistoryBaseline`, `BaselineTracker` in `ConnectionHistory.cs`, drawn by `Helpers/BaselineSeries`): the current window is shifted back 7 days by wall-clock time, read with the chart's own bucket size, then shifted forward onto the current axis. Live, it's read with 30 minutes of margin on each side and re-read only when the live window runs past it.
- **Wait Trend history** starts on the range's top waits if none of your selected types were recorded in it. The live list's default top 20 is mostly idle waits, which history doesn't store.
- The ad-hoc connection (no saved connection) isn't recorded, so the history choices are disabled for it.
