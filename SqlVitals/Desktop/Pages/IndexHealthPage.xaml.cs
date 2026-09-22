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

        await LoadFragmentationAsync(minFrag, minPages);
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
