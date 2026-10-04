# Execution Plans

An **execution plan** is the recipe SQL Server chose to run a query: which indexes it reads, how it joins tables, where it sorts. SqlVitals draws plans **graphically, like SSMS**, so you can see where a slow query spends its effort without leaving the app.

> **Where:** **View Plan** on 🔥 Resource Queries and 🛰️ SP Trace, and the **Now** / **Before** buttons on 🐢 Query Regressions · **Since:** 0.35.0 (earlier versions showed the XML only)

## Reading the diagram

The **Query Execution Plan** window opens on the **Graphical** tab. Each statement gets its own diagram, headed like SSMS:

> **Query 2: Query cost (relative to the batch): 87%**
> the statement text, and any **missing-index** suggestion in green

- **Data flows right to left.** The statement node (SELECT, INSERT, UPDATE…) is on the left. The operators that read the tables are on the right.
- **Each operator** shows an icon, the operation (*Index Seek*, *Hash Match*, *Key Lookup*), the logical operation or index kind (*Inner Join*, *Clustered*), the object (`[dbo].[Orders].[IX_Orders_Date] [o]`) and **Cost: n %**, its share of the statement.
- **Operators costing 25 % or more** are highlighted in the theme's warning colour. They're usually where to look first.
- **Arrows get thicker with the row count.** In an actual plan that's the actual rows; otherwise it's the estimated rows for all executions.
- **Badges:** a yellow **⚠** means the operator has warnings (spill to TempDB, missing statistics, no join predicate, implicit conversion, excessive memory grant, unmatched indexes). A parallel badge means it runs on several threads.
- **Statements without a plan** (SET, IF, RETURN) keep a single node, so the query numbers match SSMS. Statements inside `IF … ELSE` branches are listed in order.

### Actual plans

When the plan holds runtime figures (an *actual* plan), each operator also shows its **elapsed time** and **actual of estimated rows (%)**. An estimate that's **10× or more** off is shown in **red**; the tooltip explains it. Bad estimates are the usual cause of wrong join types, spills and too-small or too-large memory grants. Updating statistics ([Stale Stats](Index-and-Statistics-Maintenance.md#stale-statistics)) is the first thing to try.

Plans from the plan cache and Query Store are *estimated* plans, so they don't have these figures. An actual plan saved from SSMS does.

## Working with it

| To… | Do this |
|---|---|
| See an operator's key figures | **Hover** it, as with the SSMS tooltip. Hover an arrow for its rows, row size and data size. |
| See **every** property | **Click** the operator. The **Properties** panel lists a summary first, then every attribute and child element as an expandable tree. Right-click a value to **Copy value**. |
| Find an operator | Type in **Find** (Ctrl+F). Matching operators (name, object or predicate) are highlighted; **Enter** / **Shift+Enter** move between them, **Esc** clears. |
| Zoom | **−** / **+**, **Ctrl+mouse wheel** (zooms around the pointer), **Fit** for the whole plan, **1:1** for actual size (Ctrl+0) |
| Pan | Drag the background, or middle-drag anywhere |
| Copy a missing-index suggestion | Right-click the green text to copy the `CREATE INDEX` statement. Review it before running it. See [Index and Statistics Maintenance](Index-and-Statistics-Maintenance.md). |
| Share the picture | **Export diagram as PNG** saves every statement at actual size (scaled down only past 16,000 pixels). |
| Get the raw plan | The **XML** tab shows the formatted XML. **Copy XML**, or **Save as .sqlplan** to open it in SSMS or Azure Data Studio. |

The diagram follows the light and dark themes, including a switch while the window is open.

## From the details drawer

From 0.63.0 a plan can also be opened from the [details drawer](Working-with-Grids.md#details-beside-the-grid):

- **Processes:** select a session, then **Show the plan**: the plan of what it is running now, or, for a session between requests (a sleeping head blocker), the cached plan of its last statement. It is drawn in the drawer; **Open in window** opens this window with it, to keep beside the grid or on a second monitor.
- **Query Store:** select a query; under **Plans** each plan that ran in the period has **Open in window**.

## When there's no diagram

- *"No execution plan available — the plan may have been evicted from cache."*: SQL Server has already evicted the plan, or the query has no cached plan. Run it again, or look for it in [Query Store](Query-Performance.md).
- **The XML can't be read** (incomplete or damaged): the parser's message is shown, followed by *"The XML tab shows the plan exactly as SQL Server returned it."*
- An **operator SqlVitals doesn't know** is drawn with a generic icon and keeps all its properties.

## Good to know

- **Read-only and offline.** Drawing a plan runs no query. The plan comes from the page that opened the window.
- **Side-by-side comparison** of two plans isn't built in yet. For Query Regressions, open **Now** and **Before** in two windows, or save both as `.sqlplan`.
- **Large plans:** a 255-operator plan opened in under a second in testing. Tooltips are built only when you hover.

---

### Under the hood

- **Parsing (Engine):** `SqlVitals/Engine/ExecutionPlans/ShowplanParser.cs` reads showplan XML into `ExecutionPlan` → `PlanStatement` → `PlanOperator`. Elements are matched by local name, so every showplan schema version reads the same way, with or without the namespace.
  - Operator cost = its subtree cost minus its children's subtree costs, as a share of the statement's tree, the way SSMS works it out.
  - Estimated executions = 1 + rebinds + rewinds. Actual rows and executions are summed over threads; elapsed time is the slowest thread.
  - `RelOp`s inside scalar expressions (subqueries) and UDF bodies aren't drawn as children.
- **Layout (Engine):** `PlanTreeLayout` puts each child one column to the right. The first child shares its parent's row and later ones start below the previous subtree, as SSMS does.
- **Drawing (Desktop):** `Controls/ExecutionPlanView` draws a `Canvas` per statement inside a `ScrollViewer` with a `LayoutTransform` zoom. Icons are original vector line art in `Helpers/PlanOperatorIcons`; no SSMS artwork and no third-party showplan control is used. The window is `Windows/QueryExecutionPlanWindow`.
- **Tests:** `Engine.Tests/ExecutionPlans/` with sample plans in `Plans/`: a single statement, a multi-statement batch with IF/ELSE, a parallel plan, an actual plan with warnings and missing indexes, and truncated XML. Added in [#73](https://github.com/aselasampath/sqlvitals/issues/73).
