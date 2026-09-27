# Working with Grids

Most SqlVitals screens show results in grids (tables). They all work the same way.

## Sort

Click a column header to sort, and click again to reverse. **Refresh keeps your sort**, so you can sort by *Avg CPU*, press Refresh, and still see the worst query at the top. The sort is kept while you stay on the page. Opening the page again starts from its default order.

## Filter

**Resource Queries** (all tabs), **Index Health** (all tabs), **Query Store** and **Query Regressions** have a **Filter rows…** box above each grid.

| You type | Shows rows that… |
|---|---|
| `orders` | contain "orders" in any visible column |
| `orders customer` | contain **both** words, anywhere in the row |
| `"dbo.Orders"` | contain that exact phrase |
| `40000` or `40,000` | show 40,000: numbers match with or without separators |
| `2026-09-23 14:05` | match a date as the grid displays it |

- Matching ignores upper and lower case.
- While a filter is on, the box shows **N of M rows**.
- **Esc** or **✕** clears it.
- **Refresh keeps the filter.**

## Copy and export

**Right-click any grid**:

| Menu item | Copies |
|---|---|
| **Copy** | The selected rows, or **every row** if none are selected, tab-separated and ready to paste into Excel or an email |
| **Copy with headers** | The same, with the column names first |
| **Export to CSV…** | Every row shown, whatever is selected, saved as a UTF-8 CSV file |

On **Resource Queries**, **⎘ Copy All** copies every row shown, with headers.

## What you see is what you get

Copy, Export to CSV, Copy All and the **script buttons** on Index Health and Stale Stats all use **the rows the filter shows, in the current sort order**. For example, to script missing indexes for one table only, type the table name in the filter and click **Generate CREATE Script**. The button reads **(N shown)** while a filter is on.

---

### Under the hood

- **Filtering:** `SqlVitals/Engine/Filtering/RowFilter.cs` parses terms (words and quoted phrases) and matches each against the displayed text **and** the raw value of every visible column. The control is `Desktop/Controls/GridFilterBox`.
- **Keeping sort and filter on reload:** pages set rows through `Desktop/Helpers/DataGridRefresh.SetItemsSource`, which reapplies the `SortDescriptions` and the filter predicate.
- **Copy / CSV:** `Desktop/Helpers/DataGridExport` adds the context menu to every grid. Text is built by `Engine/Export/DelimitedText.cs`: tab-separated for the clipboard and CSV for files. Only visible columns are exported, in display order.
