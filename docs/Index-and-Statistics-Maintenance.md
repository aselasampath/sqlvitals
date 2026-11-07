# Index and Statistics Maintenance

Two screens find index and statistics problems **and write the T-SQL to fix them**, and a third shows where the space goes and what each statistics object holds:

| Screen | Finds | Generates |
|---|---|---|
| [🔍 Index Health](#index-health) | Missing, unused and fragmented indexes | `CREATE INDEX`, `DROP INDEX`, `ALTER INDEX … REORGANIZE / REBUILD` |
| [📉 Stale Stats](#stale-statistics) | Out-of-date statistics | `UPDATE STATISTICS` |
| [📏 Table Statistics](#table-statistics) | Which tables are largest, how much of each goes to its indexes, and which statistics may mislead the optimizer | `UPDATE STATISTICS` for one object |

> 🛡️ **SqlVitals never runs these scripts.** Each one opens in a preview window where you read it, then **Copy Script** or **Save as .sql**. You decide when and where to run it, ideally in a maintenance window, after testing.

## How the script buttons work (all of them)

- **Select rows** (Ctrl+click, Shift+click) to script just those. **Select nothing** to script every row shown.
- Rows are scripted **in the grid's current sort order**.
- If a [filter](Working-with-Grids.md) is on, only the rows it shows are scripted. The button then reads **(N shown)** instead of **(all)**.
- Every statement is **guarded** (`IF EXISTS` / `IF NOT EXISTS`), so a saved script stays safe to run later, even if things have changed.
- A header at the top explains what to check before running.

---

## Index Health

> **Where:** sidebar → 🔍 Index Health · **Use it when:** planning index tuning or routine maintenance

**The verdict** (from 0.66.0) names the most valuable missing index, critical ones first (*3 missing indexes suggested, 1 critical; the most valuable on SalesDb.dbo.Orders*), in amber when one is critical: how many seeks and scans it would serve, on which columns, and how many unused indexes only cost writes. **Show it** opens the Missing Indexes tab with it selected and its `CREATE` statement in the [details drawer](Working-with-Grids.md#details-beside-the-grid), with **Copy**; **Unused indexes** opens that tab. See [The page template](Getting-Started.md#verdict-first-then-the-detail).

![Index Health: the verdict names the most valuable missing index, above the four tabs](images/page-template/index-health.png)

Four tabs, which fill the page and are kept when you come back to it (from 0.66.0).

### Missing Indexes

Indexes SQL Server's optimizer wished it had, sorted by **Severity** then **Impact Score**.

| Column | Meaning |
|---|---|
| **Impact Score** | How much this index could help, combining cost, estimated improvement and how often it would be used |
| **Seeks / Scans** | How many times queries could have used it |
| **Equality Cols / Include Cols** | The suggested key and `INCLUDE` columns |
| **Severity** | **CRITICAL** ≥ 80 % estimated improvement · **WARNING** ≥ 50 % · **INFO** below |
| **CREATE Statement** | A ready-made statement for this row |

**Generate CREATE Script** builds one script with a generated name (`IX_<Table>_<key columns>`) and an `IF NOT EXISTS` guard per index.

> ⚠️ **Don't create them all.** Suggestions often overlap. Several may be served by one wider index. Each new index slows down inserts and updates. Review them against the existing indexes first.

### Unused Indexes

Indexes with **zero reads since the last restart**, which still cost writes (**User Updates**).

**Generate DROP Script** builds a `DROP INDEX` script. **Unique indexes are commented out**, because they may enforce a business rule even if nothing reads them.

> ⚠️ "Since the last restart" matters. If the server restarted yesterday, a month-end report's index will look unused. Check how long the server has been up before dropping anything.

### Index Usage

Seeks, scans, lookups and updates per index, with a **Health** assessment and when it was last sought and scanned. Use it to spot indexes that are written far more than read.

### Fragmentation

How fragmented each index is, filtered by:

- **Min Page Count** (default 1000). Small indexes don't benefit from maintenance.
- **Min Frag %** (default 10).

Change them and press **↻ Refresh** (or **Enter**). Only this tab reloads, and it keeps its sort.

**Maintenance Script** settings:

| Setting | Default | Meaning |
|---|---|---|
| **Reorganize from** | 5 % | From here up to the rebuild threshold → `REORGANIZE` (light, always online) |
| **Rebuild from** | 30 % | At or above this → `REBUILD` (thorough, heavier) |
| **ONLINE = ON** | On where supported | Rebuild without locking the table. Only enabled on editions that support it. |

**Generate Maintenance Script** writes one `ALTER INDEX` per index. Indexes below the page count, and heaps (tables without a clustered index), are skipped, with the reason written into the script.

---

## Stale Statistics

> **Where:** sidebar → 📉 Stale Stats · **Use it when:** queries got slower after lots of data changed, or plans look badly estimated

**Statistics** tell SQL Server's optimizer how data is distributed. When lots of rows change, they go out of date and the optimizer makes poor choices. This screen lists statistics with pending modifications, most-modified first. To see what one holds, double-click its row or press **Open in Table Statistics** (from 0.87.0): [Table Statistics](#opened-from-another-page) opens on that database, table and statistics object.

**The verdict** (from 0.66.0) names the statistics with 20 % or more of their table's rows changed since they were updated, the largest share first (*1 statistic has 20 % or more of its table changed; the most, SalesDb.dbo.OrderLines (…)*), in amber, with how many rows changed and when it was last updated. With none that stale, it names the most changed one anyway. **Show it** selects it, so **Generate UPDATE STATISTICS Script** scripts just that one. From 0.79.1 a refresh keeps the statistic selected and the grid's sort. What statistics are for folds to one line. See [The page template](Getting-Started.md#verdict-first-then-the-detail).

![Stale Stats: the verdict names the most changed statistic, above the statistics](images/page-template/stale-stats.png)

| Column | Meaning |
|---|---|
| **Last Updated**, **Days Old** | When the statistic was last rebuilt. Blank when it never has been: it was built on an empty table |
| **Rows**, **Modifications** | Table size when it was last rebuilt (blank if never), and rows changed since then |
| **Sample %** | How much of the table was read last time |
| **Auto Created / User Created** | Who created it |

**Sampling** for the script:

| Choice | Does | When |
|---|---|---|
| **Default** | SQL Server picks the sample size (or reuses a persisted sample percent). Fast. | Most of the time |
| **FULLSCAN** | Reads every row for the most accurate statistics. Heavy I/O. | Skewed data where sampled statistics gave bad plans. Run it off-peak. |

**Generate UPDATE STATISTICS Script** writes one `UPDATE STATISTICS <schema>.<table> (<statistic>)` per row, each guarded by `IF EXISTS`. The script warns you when:

- a statistic was last built with a **full scan** and you chose Default (a sampled update would make it *less* accurate);
- you chose **FULLSCAN** (it gives the approximate number of rows that will be read);
- a statistic is **incremental** (it suggests `WITH RESAMPLE ON PARTITIONS`).

---

### Under the hood

| | Source | Script generator |
|---|---|---|
| Missing indexes | `sys.dm_db_missing_index_details`, `_groups`, `_group_stats` | `Engine/Scripting/MissingIndexCreateScript.cs` |
| Unused / usage | `sys.dm_db_index_usage_stats` + `sys.indexes` | `Engine/Scripting/UnusedIndexDropScript.cs` |
| Fragmentation | `sys.dm_db_index_physical_stats` | `Engine/Scripting/IndexMaintenanceScript.cs` |
| Stale statistics | `sys.stats` + `sys.dm_db_stats_properties` | `Engine/Scripting/UpdateStatisticsScript.cs` |
| Table Statistics: health and details (ratings in `Engine/TableStatistics/StatisticsFindings.cs`) | `sys.stats`, `sys.dm_db_stats_properties`, `sys.dm_db_stats_histogram`, `DBCC SHOW_STATISTICS` | `Engine/Scripting/UpdateStatisticsScript.cs` |

- **Impact Score** = `(user_seeks + user_scans) × avg_total_user_cost × avg_user_impact / 100`.
- **Severity** for missing indexes is `avg_user_impact` ≥ 80 → CRITICAL, ≥ 50 → WARNING.
- **ONLINE = ON** is offered only when `SERVERPROPERTY('EngineEdition')` is 3 (Enterprise/Developer), 5 (Azure SQL Database) or 8 (Managed Instance). Even then it's left off index types that reject it (columnstore, XML, spatial), with a note in the script.
- `dm_db_index_physical_stats` returns one row per partition. The maintenance script **collapses** them into one statement per index.
- Script generators are pure functions (rows + options → text) with unit tests in `SqlVitals.Engine.Tests`. The preview is `Desktop/Windows/SqlScriptWindow`.
- Usage DMVs reset on restart, and on Azure SQL also on failover or scaling. That's why *unused* always means "since the last restart".
- Names from the server (tables, indexes, statistics, users) can hold line breaks. In a script's `--` comments a line break would end the comment and run the rest of the name, so every name in a comment is kept to one line (`Engine/Scripting/ScriptText`). In live statements a name stays inside `[...]` or `N'...'`, where a line break is part of it. Added in 0.44.1 (#109).
- A statistic that has never been updated has no `STATS_DATE` and no row counts in `sys.dm_db_stats_properties`; they read as blank. Before 0.44.1 such a statistic made the whole page fail. `sys.stats.is_incremental` is SQL Server 2014+, so it's only named where `sys.all_columns` says it exists.
- The **usage** tab takes the table's name from `sys.indexes`: before 0.44.1 it came from the usage row, which an index that was never used doesn't have, so exactly the *Unused* rows had no table name.

---

## Table Statistics

> **Where:** sidebar → Maintenance → Table Statistics · **Use it when:** finding where a database's space goes and which indexes may not be worth it, or [why a query got a bad row estimate](How-To-Recipes.md#why-did-this-query-get-a-bad-row-estimate)

From 0.85.0; statistics health and the Statistics tab from 0.86.0. Nothing runs until you open the page, and it never polls: it reads again on **Refresh**, or when you choose another database or table. It uses documented views and functions and `DBCC SHOW_STATISTICS`, reads no table rows and changes nothing. See [Server Impact and Permissions](Server-Impact-and-Permissions.md#table-statistics-reading-statistics) for the permission it needs to read statistics.

### Choose a database

On SQL Server the **Database** box at the top right lists the online databases the login can open, starting on the connection's. Choosing one reads that database for this page only: the connection and the other pages stay where they were. On Azure SQL Database, where a connection can't change database, the page shows the connected database and no box.

### The tables

One row per user table, largest first:

| Column | Meaning |
|---|---|
| **Rows** | Rows in the heap or clustered index, added up across partitions |
| **Data size** | Space reserved for the heap or clustered index, LOB and row-overflow included. A clustered index is the table's data, so it counts here |
| **Index size** | Space reserved for its other indexes |
| **Total size** | Data + index size, with a bar against the largest table. It matches `sp_spaceused`'s *reserved* for a table with no XML or spatial index |
| **Statistics health** | The worst rating among the table's statistics: **Good**, **Fair**, **Poor** or **Locked**, or a dash, with a tooltip saying why, when there's nothing to rate or the login can't read them. Sorting on it puts the worst first |
| **Most changed** | The largest share of rows changed since one of its statistics was last updated |
| **Oldest update** | When the least recently updated of its statistics was last updated |

The verdict names the largest table and how much of it its indexes take; **Show it** selects it. Memory-optimized tables show as 0, because their memory isn't in these pages.

**Locked** means a schema change (`ALTER TABLE`, an offline rebuild, `TRUNCATE`) holds the table right now. The other tables still load, without waiting for it, and **Refresh** reads it again once the change is over.

### Statistics health

Sizes show first. Health then fills in, largest table first, 50 tables at a time, and **Rating statistics: N of M tables** shows while it reads. **Refresh**, choosing another database and leaving the page cancel it.

Health rates each statistics object from its header only: changes, sample rate, and whether it was ever built. That keeps it short enough to run over a whole database. A columnstore index never has a histogram, so it isn't rated.

| Rating | When |
|---|---|
| **Poor** | Modifications are past the auto-update threshold |
| **Fair** | A frequent value hidden in a range, or a low sample rate, as on the [Statistics tab](#the-statistics-tab). The grid can't see those, so there it means statistics never built on a table with rows, or a sample under 1 % of a million rows or more |
| **Good** | None of those. Skew, parameter sensitivity, few steps and NULL share leave it Good: the statistics describe the data correctly, and what to do is in the query |

**The auto-update threshold:** 500 changes for a table of up to 500 rows, including statistics built on an empty table. Above that, at compatibility level 130 and later, the lower of `500 + 20 % of rows` and `SQRT(1000 × rows)`; below 130, `500 + 20 % of rows`. A million rows at level 130 gives 31,623.

An object you open can rate differently from the grid. It usually rates lower, because the tab can see its density and histogram. It can also rate higher: a sample under 1 % is Fair in the grid, but does little harm on a nearly unique column, so the tab leaves it Good.

### A table's sizes

On the **Sizes** tab, select a table to see its data, indexes, total and rows, then:

- **A bar** of the whole table: its data, each index, and any flagged index in amber. Hover a part for its name and size. It always shows the whole table, whatever the grid's filters hide.
- **A grid of its indexes:** name, type, rows, reserved and used size, share of the table's total, **Reads** (seeks + scans + lookups) and **Writes**.

Reads and writes count from the last restart, and the page says when that was. A count that's low soon after a restart doesn't mean much.

**A flagged index** reads *Few reads, many writes*, with a link to [Index Health](#index-health). It's flagged when it's a nonclustered index of at least 100 MB that doesn't enforce uniqueness, written at least 1,000 times, and read at most once per 10 writes. That makes it worth a look, not a reason to drop it. Index Health reads the connection's database: when another one is chosen here, the page says so.

Choosing another table or database cancels what's still being read for the last one, on the server too. Space used by XML and spatial indexes' internal tables isn't counted.

### Opened from another page

From 0.87.0 you can follow a problem here without finding the table again:

| From | How | Opens on |
|---|---|---|
| [Stale Stats](#stale-statistics) | Double-click a row, or **Open in Table Statistics** | The database and table, on the **Statistics** tab with that statistics object |
| [Index Health](#index-health): **Unused Indexes** or **Fragmentation** | Double-click a row, or **Open in Table Statistics** | The database and table, on **Sizes** |
| An [execution plan](Execution-Plans.md#working-with-it), in its window or the details drawer | Click an operator on a table, then **Open Table Statistics** in **Properties** | The database and table, on **Statistics** if the plan names the statistics the optimizer used |

A banner says which page it was opened from, **Back** returns there with its selection as it was, and the active connection doesn't change.

If it can't open what was asked for, it says so under the tables, and the page still works:

| What happened | What it does |
|---|---|
| The table was dropped or renamed, or the login can't see it | Stays on the database, with no table selected. **Refresh** looks again |
| The statistics object is gone | Opens the table on another statistics object |
| The login can't open that database, or it's offline | Shows the connection's database |
| Azure SQL Database, and the target is in another database | Shows the connected database, and says to connect to the other one |
| The plan came from a server you've since switched away from | Doesn't look for the table here: one of the same name would be a different table |

### The Statistics tab

Select a table and open **Statistics**. It reads only while the tab is showing, and choosing another object cancels what's still being read. From top to bottom:

**Statistics object** lists the table's statistics, index and column ones, with a line saying which kind it is and which column its histogram is on. The tab starts on the clustered index's statistics. With no clustered index, or a clustered columnstore index, it starts on the first statistics object that isn't on a columnstore index.

**FULLSCAN** and **Generate UPDATE STATISTICS Script** open a script for the chosen object in a window for review, as on [Stale Stats](#stale-statistics). SqlVitals never runs it.

**A login that can't read the object** (from 0.87.0) sees *This login can't read these statistics*, what would allow it and what that also allows, in place of the figures and chart, and the script is off. Another object on the same table may still be readable, as a grant can be per column. See [Reading statistics](Server-Impact-and-Permissions.md#table-statistics-reading-statistics).

**The figures:**

| Figure | Meaning |
|---|---|
| **Last updated** | When these statistics were last built. Plans compiled since use what they said then |
| **Rows** | Rows in the table, or in the filter, when they were last built |
| **Rows sampled**, **Sample rate** | Rows read to build them, and that as a share of rows |
| **Steps** | Steps in the histogram: at most 200, and one for NULL |
| **Modifications** | Changes to the leading column since they were last built |

**The verdict and What to look at** give the object's rating, then each finding: what was found and what to try. Finding text names steps by number, never by key.

| Finding | Shows when | Rating | Try |
|---|---|---|---|
| **Past the auto-update threshold** | Modifications are past the [threshold](#statistics-health) | Poor | Check `AUTO_UPDATE_STATISTICS` is on and a query has compiled since, or generate the script |
| **Low sample rate** | A sample under 10 %, fewer than 10 rows read per distinct value, and distinct values under 90 % of the rows | Fair | `FULLSCAN`, or a higher `SAMPLE` with `PERSIST_SAMPLE_PERCENT = ON` |
| **Frequent value hidden in a range** | A step's `AVG_RANGE_ROWS` is at least 100, and 10× or more above the steps beside it | Fair | `FULLSCAN`, which can give the value a step of its own, or filtered statistics |
| **Skew** | One step holds 25 % or more of the rows, and its key's rows or its average per value is 10× or more above the unknown-value estimate | Good | A filtered index or filtered statistics |
| **Parameter sensitivity** | A step key's `EQ_ROWS` is 10× or more above or below the unknown-value estimate | Good | `OPTION (RECOMPILE)` or `OPTIMIZE FOR`, and compare the plans |
| **Few steps** | Under 100 steps, with 1,000 or more distinct values per step | Good | Fine for evenly spread values. If not, filtered statistics for the busy ranges |
| **NULL share** | The NULL step holds 25 % or more of the rows | Good | A filtered index or filtered statistics with `WHERE column IS NOT NULL` |

Only the first two apply to a histogram of under 1,000 rows, whose shape doesn't move a plan enough to mention.

**Histogram** is a chart with one column per step, in step order: `EQ_ROWS` and `RANGE_ROWS` stacked, the key below, and a dashed line at the unknown-value estimate. Hover a step for its values and how the optimizer uses them. **Log scale** keeps small steps visible next to a huge one.

**The histogram grid** holds the same steps, with **Filter rows…**, **Copy All** and a **Steps** choice: **All steps**, **Keys 10x or more from the unknown-value estimate** or **Above-average AVG_RANGE_ROWS**. The chart and the findings always show the whole object, whatever the grid's filters hide.

**Density vector** has **Filter rows…** and **Copy All**:

| Column | Meaning |
|---|---|
| **Column prefix** | The leading column, then it with the next, and so on. A nonclustered index's vector also includes the clustering keys, which SQL Server adds to its rows |
| **All density** | 1 / the distinct values of the prefix |
| **Distinct values** | 1 / all density: also the estimate for `GROUP BY` on the prefix |
| **Average rows per value** | All density × rows |

**No histogram:** a message says so instead of an empty chart: the statistics were never built, or the table is empty. A columnstore index has none, and has no script, as `UPDATE STATISTICS` fails on one.

### Reading a histogram

| The query asks for | The estimate comes from |
|---|---|
| `= value`, a step's key | `EQ_ROWS` |
| `= value` between two keys | `AVG_RANGE_ROWS` (`RANGE_ROWS` / `DISTINCT_RANGE_ROWS`), whatever the value's real count |
| `= @variable` the optimizer can't see | The unknown-value estimate: all density × rows, the dashed line |
| `IS NULL` | The NULL step's `EQ_ROWS` |
| `GROUP BY` on a column prefix | **Distinct values** in the density vector |

See the recipe [Why did this query get a bad row estimate?](How-To-Recipes.md#why-did-this-query-get-a-bad-row-estimate)

### Histogram keys are real values

`RANGE_HI_KEY` values are real values from the table, and could be emails or names. They show only in the chart, its tooltip and the histogram grid. They aren't written to the history database, the activity log or notifications, and Export for AI doesn't include them. **Copy**, **Copy All** and **Export to CSV** on the grid do include them, because you're plainly exporting that grid.

### Filters

Both grids work as described in [Working with Grids](Working-with-Grids.md#filter): **Copy**, **Copy All** and **Export to CSV** take the rows shown, and **Refresh** and leaving the page keep the filters. The tables also have a **Schema** filter and a **Health** filter (Fair, Good, Locked, Poor) that take several values; a table whose health hasn't arrived matches no Health choice until it does. The indexes have a **Type** filter and **Flagged only**. **Clear filters** turns off every filter on its grid. Filtering the tables never changes the table selected, even when it hides that row.

**Refresh** and leaving the page also keep the tab, the statistics object, the **Steps** choice, **Log scale** and **FULLSCAN**.
