using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Desktop.Pages;

public partial class IndexHealthPage : System.Windows.Controls.Page, IRefreshable
{
    private const double DefaultMinFragPct   = 10;
    private const long   DefaultMinPageCount = 1000;

    private readonly IWaitStatsRepository _repo;

    // Bumped on every fragmentation load so a slower, older load (e.g. the sidebar Refresh
    // overlapping the in-page one) cannot overwrite the grid with results for stale criteria.
    private int _fragLoadVersion;

    // One edition probe per page instance: MainWindow builds a fresh page after a connection
    // switch, so the answer cannot go stale underneath us.
    private bool _onlineSupportChecked;

    public IndexHealthPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        // The sidebar Refresh has nowhere to report a validation problem, so it keeps the
        // old behaviour of falling back to defaults — and writes them back into the boxes
        // so the criteria on screen always match the rows in the grid.
        if (!TryReadFragCriteria(out double minFrag, out long minPages, out _, out _))
        {
            minFrag  = DefaultMinFragPct;
            minPages = DefaultMinPageCount;
            TxtMinFragPct.Text   = minFrag.ToString(CultureInfo.CurrentCulture);
            TxtMinPageCount.Text = minPages.ToString(CultureInfo.CurrentCulture);
        }

        var (missing, unused) = await _repo.GetIndexHealthAsync();
        var usage = await _repo.GetIndexUsagePatternsAsync();

        MissingGrid.ItemsSource = missing
            .OrderBy(m => m.Severity == "CRITICAL" ? 1 : m.Severity == "WARNING" ? 2 : 3)
            .ThenByDescending(m => m.ImpactScore)
            .ToList();
        UnusedGrid.ItemsSource  = unused.OrderByDescending(u => u.UserUpdates).ToList();
        UsageGrid.ItemsSource   = usage.ToList();
        UpdateCreateScriptButton();
        UpdateDropScriptButton();

        await UpdateOnlineRebuildSupportAsync();
        await LoadFragmentationAsync(minFrag, minPages);
    }

    /// <summary>
    /// ONLINE = ON is an edition feature — offering it where the server rejects it would only
    /// produce a script that fails. Ask once, then enable the box (and default it on, since an
    /// online rebuild is the one that doesn't take the table away from the application).
    /// </summary>
    private async System.Threading.Tasks.Task UpdateOnlineRebuildSupportAsync()
    {
        if (_onlineSupportChecked) return;
        _onlineSupportChecked = true;

        try
        {
            if (await _repo.SupportsOnlineIndexRebuildAsync())
            {
                ChkOnlineRebuild.IsEnabled = true;
                ChkOnlineRebuild.IsChecked = true;
                ChkOnlineRebuild.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary");
                ChkOnlineRebuild.ToolTip = "Rebuild rowstore indexes with WITH (ONLINE = ON) so the table stays available. "
                    + "Slower, and it needs more log and tempdb space.";
            }
            else
            {
                ChkOnlineRebuild.ToolTip = "This server's edition does not support online index rebuilds — "
                    + "the script would fail. Enterprise, Developer, Azure SQL Database and Managed Instance do.";
            }
        }
        catch
        {
            // The edition check failed (server unreachable, permissions). Leave the box off:
            // a rebuild without ONLINE runs everywhere, one with it does not.
            _onlineSupportChecked = false;
            ChkOnlineRebuild.ToolTip = "Could not read the server edition, so online rebuilds are not offered. "
                + "Refresh to try again.";
        }
    }

    private void MissingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCreateScriptButton();

    private void UpdateCreateScriptButton()
    {
        int selected = MissingGrid.SelectedItems.Count;
        int total    = MissingGrid.Items.Count;

        BtnCreateScript.IsEnabled = total > 0;
        BtnCreateScript.Content = selected > 0
            ? $"Generate CREATE Script ({selected} selected)"
            : "Generate CREATE Script (all)";
    }

    private void BtnCreateScript_Click(object sender, RoutedEventArgs e)
    {
        // Selected rows if the user picked some, otherwise everything in the grid —
        // in the grid's current sort order so the script reads like what's on screen.
        var selected = MissingGrid.SelectedItems.OfType<MissingIndex>().ToHashSet();
        var suggestions = MissingGrid.Items.OfType<MissingIndex>()
            .Where(i => selected.Count == 0 || selected.Contains(i))
            .ToList();
        if (suggestions.Count == 0) return;

        var notice =
            $"Review before running. {suggestions.Count:N0} index(es) will be created. " +
            "SQL Server does not check these suggestions against your existing indexes, and every new index slows writes. SqlVitals does not run this script.";

        new SqlScriptWindow(
            "Create Missing Indexes",
            notice,
            MissingIndexCreateScript.Build(suggestions, DateTime.Now),
            $"CreateMissingIndexes_{DateTime.Now:yyyyMMdd_HHmm}.sql")
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }

    private void UnusedGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDropScriptButton();

    private void UpdateDropScriptButton()
    {
        int selected = UnusedGrid.SelectedItems.Count;
        int total    = UnusedGrid.Items.Count;

        BtnDropScript.IsEnabled = total > 0;
        BtnDropScript.Content = selected > 0
            ? $"Generate DROP Script ({selected} selected)"
            : "Generate DROP Script (all)";
    }

    private void BtnDropScript_Click(object sender, RoutedEventArgs e)
    {
        // Selected rows if the user picked some, otherwise everything in the grid —
        // in the grid's current sort order so the script reads like what's on screen.
        var selected = UnusedGrid.SelectedItems.OfType<UnusedIndex>().ToHashSet();
        var indexes = UnusedGrid.Items.OfType<UnusedIndex>()
            .Where(i => selected.Count == 0 || selected.Contains(i))
            .ToList();
        if (indexes.Count == 0) return;

        int uniqueCount = indexes.Count(i => i.IsUnique);
        var notice =
            $"Review before running. {indexes.Count - uniqueCount:N0} index(es) will be dropped" +
            (uniqueCount > 0 ? $"; {uniqueCount:N0} unique index(es) are commented out" : "") +
            ". Usage stats reset on every SQL Server restart — confirm the uptime in the script covers a full business cycle. SqlVitals does not run this script.";

        new SqlScriptWindow(
            "Drop Unused Indexes",
            notice,
            UnusedIndexDropScript.Build(indexes, DateTime.Now),
            $"DropUnusedIndexes_{DateTime.Now:yyyyMMdd_HHmm}.sql")
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }

    private void FragGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMaintenanceScriptButton();

    private void UpdateMaintenanceScriptButton()
    {
        int selected = FragGrid.SelectedItems.Count;
        int total    = FragGrid.Items.Count;

        BtnMaintenanceScript.IsEnabled = total > 0;
        BtnMaintenanceScript.Content = selected > 0
            ? $"Generate Maintenance Script ({selected} selected)"
            : "Generate Maintenance Script (all)";
    }

    private void BtnMaintenanceScript_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadScriptThresholds(out var options, out string error, out var invalidBox))
        {
            SetFragStatus(error, isError: true);
            invalidBox?.Focus();
            invalidBox?.SelectAll();
            return;
        }

        // Selected rows if the user picked some, otherwise everything in the grid —
        // in the grid's current sort order so the script reads like what's on screen.
        var selected = FragGrid.SelectedItems.OfType<IndexFragmentation>().ToHashSet();
        var indexes = FragGrid.Items.OfType<IndexFragmentation>()
            .Where(i => selected.Count == 0 || selected.Contains(i))
            .ToList();
        if (indexes.Count == 0) return;

        var summary = IndexMaintenanceScript.Summarize(indexes, options);
        var notice =
            $"Review before running. {summary.Rebuild:N0} index(es) will be rebuilt and {summary.Reorganize:N0} reorganized" +
            (summary.Skipped > 0 ? $"; {summary.Skipped:N0} skipped" : "") +
            (options.OnlineRebuild ? ", rebuilds using ONLINE = ON" : "") +
            ". A rebuild without ONLINE = ON locks the table for the whole operation — run this in a maintenance window. SqlVitals does not run this script.";

        new SqlScriptWindow(
            "Index Maintenance",
            notice,
            IndexMaintenanceScript.Build(indexes, options, DateTime.Now),
            $"IndexMaintenance_{DateTime.Now:yyyyMMdd_HHmm}.sql")
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
    }

    /// <summary>
    /// Reads the script thresholds. Min page count is shared with the grid criteria above —
    /// an index too small to be worth defragmenting is the same index in both places.
    /// </summary>
    private bool TryReadScriptThresholds(out IndexMaintenanceOptions options, out string error, out TextBox? invalidBox)
    {
        options = new IndexMaintenanceOptions();
        error = string.Empty;
        invalidBox = null;

        if (!double.TryParse(TxtReorganizePct.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double reorg)
            || reorg <= 0 || reorg > 100)
        {
            error = "Reorganize from % must be a number greater than 0 and at most 100.";
            invalidBox = TxtReorganizePct;
            return false;
        }

        if (!double.TryParse(TxtRebuildPct.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double rebuild)
            || rebuild <= 0 || rebuild > 100)
        {
            error = "Rebuild from % must be a number greater than 0 and at most 100.";
            invalidBox = TxtRebuildPct;
            return false;
        }

        if (rebuild < reorg)
        {
            error = "Rebuild from % must be at least the reorganize threshold — a rebuild is the heavier fix.";
            invalidBox = TxtRebuildPct;
            return false;
        }

        if (!long.TryParse(TxtMinPageCount.Text, NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out long minPages) || minPages <= 0)
        {
            error = "Min Page Count must be a whole number greater than 0.";
            invalidBox = TxtMinPageCount;
            return false;
        }

        options = new IndexMaintenanceOptions
        {
            ReorganizeThresholdPercent = reorg,
            RebuildThresholdPercent    = rebuild,
            MinPageCount               = minPages,
            OnlineRebuild              = ChkOnlineRebuild.IsEnabled && ChkOnlineRebuild.IsChecked == true,
        };
        return true;
    }

    private async void BtnRefreshFrag_Click(object sender, RoutedEventArgs e) => await RefreshFragmentationAsync();

    private async void FragParam_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RefreshFragmentationAsync();
    }

    private async System.Threading.Tasks.Task RefreshFragmentationAsync()
    {
        // Enter in a text box bypasses the disabled button — ignore it while a load is running.
        if (!BtnRefreshFrag.IsEnabled) return;

        if (!TryReadFragCriteria(out double minFrag, out long minPages, out string error, out var invalidBox))
        {
            SetFragStatus(error, isError: true);
            invalidBox?.Focus();
            invalidBox?.SelectAll();
            return;
        }

        try
        {
            await LoadFragmentationAsync(minFrag, minPages);
        }
        catch
        {
            // Already reported inline by LoadFragmentationAsync.
        }
    }

    /// <summary>
    /// Reloads only the fragmentation grid — the other tabs don't depend on these criteria,
    /// so they keep their rows, sort and scroll position. Reports progress and failure inline;
    /// rethrows so the sidebar Refresh can report it in the status bar too.
    /// </summary>
    private async System.Threading.Tasks.Task LoadFragmentationAsync(double minFrag, long minPages)
    {
        var version = ++_fragLoadVersion;
        BtnRefreshFrag.IsEnabled = false;
        SetFragStatus("Loading…", isError: false);
        try
        {
            var rows = (await _repo.GetIndexFragmentationAsync(minFrag, minPages)).ToList();
            if (version != _fragLoadVersion) return;

            SetItemsSourcePreservingSort(FragGrid, rows);
            UpdateMaintenanceScriptButton();
            SetFragStatus(
                $"{rows.Count:N0} index{(rows.Count == 1 ? "" : "es")} with ≥ {minFrag:0.##}% fragmentation " +
                $"and ≥ {minPages:N0} pages · updated {DateTime.Now:HH:mm:ss}",
                isError: false);
        }
        catch (Exception ex)
        {
            if (version == _fragLoadVersion)
                SetFragStatus(MainWindow.FormatErrorStatus(ex) + " — the grid still shows the previous results.", isError: true);
            throw;
        }
        finally
        {
            if (version == _fragLoadVersion)
                BtnRefreshFrag.IsEnabled = true;
        }
    }

    private bool TryReadFragCriteria(out double minFrag, out long minPages, out string error, out TextBox? invalidBox)
    {
        error = string.Empty;
        invalidBox = null;
        minPages = 0;

        if (!double.TryParse(TxtMinFragPct.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out minFrag)
            || minFrag <= 0 || minFrag > 100)
        {
            error = "Min Frag % must be a number greater than 0 and at most 100.";
            invalidBox = TxtMinFragPct;
            return false;
        }

        if (!long.TryParse(TxtMinPageCount.Text, NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out minPages) || minPages <= 0)
        {
            error = "Min Page Count must be a whole number greater than 0.";
            invalidBox = TxtMinPageCount;
            return false;
        }

        return true;
    }

    private void SetFragStatus(string text, bool isError)
    {
        TxtFragStatus.Text = text;
        TxtFragStatus.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Danger" : "TextMuted");
    }

    // Assigning a new ItemsSource creates a new collection view, which drops the user's
    // column sort. Carry it across so a refresh doesn't reshuffle what they were looking at.
    private static void SetItemsSourcePreservingSort(DataGrid grid, System.Collections.IEnumerable items)
    {
        var sorts = grid.Items.SortDescriptions.ToList();

        grid.ItemsSource = items;

        foreach (var sort in sorts)
            grid.Items.SortDescriptions.Add(sort);
        foreach (var column in grid.Columns)
        {
            var match = sorts.FirstOrDefault(s => s.PropertyName == column.SortMemberPath);
            column.SortDirection = match.PropertyName is null ? null : match.Direction;
        }
    }
}
