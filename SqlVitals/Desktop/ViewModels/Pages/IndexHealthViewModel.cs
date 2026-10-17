using System.Collections;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Scripting;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Index Health (#150, #187), from the connection's database: the missing indexes SQL Server asks
/// for, the unused ones, how each index is used, and the fragmented ones, each with a script to
/// review (nothing is run). Read once and on Refresh; the fragmentation can be read again on its
/// own with other criteria, cancelling a read of it still running. The verdict names the missing
/// indexes asked for most, and the unused ones.
/// </summary>
public sealed partial class IndexHealthViewModel(IIndexRepository repo, IServerRepository server, IDialogService dialogs,
                                                 TimeProvider? time = null) : PageViewModel
{
    /// <summary>The tabs: missing, unused, usage, fragmentation.</summary>
    internal const int MissingTab = 0, UnusedTab = 1, UsageTab = 2, FragmentationTab = 3;

    internal const double DefaultMinFragPct   = 10;
    internal const long   DefaultMinPageCount = 1000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The tab shown.</summary>
    [ObservableProperty]
    private int _tabIndex = MissingTab;

    // ── Missing, unused and usage ─────────────────────────────────────────

    /// <summary>The missing indexes, critical first, then by impact.</summary>
    public GridRows<MissingIndex> Missing { get; } = [];

    /// <summary>The missing index in the drawer; kept across a refresh by its table and columns.</summary>
    [ObservableProperty]
    private MissingIndex? _selectedMissing;

    /// <summary>The unused indexes, the most updated first.</summary>
    public GridRows<UnusedIndex> Unused { get; } = [];

    [ObservableProperty]
    private UnusedIndex? _selectedUnused;

    /// <summary>How each index is read and written.</summary>
    public GridRows<IndexUsagePattern> Usage { get; } = [];

    [ObservableProperty]
    private IndexUsagePattern? _selectedUsage;

    /// <summary>What the CREATE, DROP and maintenance scripts cover: the rows selected, or those shown.</summary>
    public ScriptScope MissingScope { get; } = new("CREATE");
    public ScriptScope UnusedScope { get; } = new("DROP");
    public ScriptScope FragmentationScope { get; } = new("Maintenance");

    /// <summary>A missing index to show: the verdict's Show it selected it. The view scrolls to it, past a search, and opens the drawer.</summary>
    public event Action<MissingIndex>? ShowRow;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        // The top bar's Refresh has nowhere to report a validation problem, so it falls back to
        // the defaults, and writes them back into the boxes so the criteria on screen always match
        // the rows in the grid.
        if (FragmentationCriteria() is not { } criteria)
        {
            MinFragText  = DefaultMinFragPct.ToString(CultureInfo.CurrentCulture);
            MinPagesText = DefaultMinPageCount.ToString(CultureInfo.CurrentCulture);
            criteria     = (DefaultMinFragPct, DefaultMinPageCount);
        }

        _judged = false;
        var (missing, unused) = await repo.GetIndexHealthAsync(cancel);
        var usage = await repo.GetIndexUsagePatternsAsync(cancel);
        var missingList = missing.ToList();
        var unusedList  = unused.ToList();
        Verdict = IndexHealthVerdict.From(missingList, unusedList);
        _judged = true;

        // A refresh keeps each grid's column sort and filter; the order below is only the default.
        var (keptMissing, keptUnused, keptUsage) = (SelectedMissing, SelectedUnused, SelectedUsage);
        Missing.Replace(missingList
            .OrderBy(m => m.Severity == "CRITICAL" ? 1 : m.Severity == "WARNING" ? 2 : 3)
            .ThenByDescending(m => m.ImpactScore));
        Unused.Replace(unusedList.OrderByDescending(u => u.UserUpdates));
        Usage.Replace(usage);
        SelectedMissing = Missing.FirstOrDefault(m => keptMissing is not null && Key(m) == Key(keptMissing));
        SelectedUnused  = Unused.FirstOrDefault(u => keptUnused is not null && Key(u) == Key(keptUnused));
        SelectedUsage   = Usage.FirstOrDefault(u => keptUsage is not null && Key(u) == Key(keptUsage));

        await CheckOnlineRebuildAsync(cancel);
        await ReadFragmentationAsync(criteria.MinFrag, criteria.MinPages, cancel);
    }

    // Whether this read judged the missing and unused indexes. A failed fragmentation read after
    // that is said in its own status and the top bar, and the verdict stays on what was read.
    private bool _judged;

    protected override void ShowLoadError(Exception exception)
    {
        if (!_judged)
            ShowError("the index statistics", exception);
    }

    private static (string, string, string, string?, string?, string?) Key(MissingIndex m) =>
        (m.DatabaseName, m.SchemaName, m.TableName, m.EqualityColumns, m.InequalityColumns, m.IncludedColumns);

    private static (string, string, string, string) Key(UnusedIndex u) => (u.DatabaseName, u.SchemaName, u.TableName, u.IndexName);

    private static (string, string, string?) Key(IndexUsagePattern u) => (u.DatabaseName, u.TableName, u.IndexName);

    private static (string, string, string, string?, string) Key(IndexFragmentation f) =>
        (f.DatabaseName, f.SchemaName, f.TableName, f.IndexName, f.IndexType);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case IndexHealthVerdict.ShowMissing: ShowTopMissing();     break;
            case IndexHealthVerdict.ShowUnused:  TabIndex = UnusedTab; break;
        }
    }

    // The verdict's Show it: the Missing Indexes tab, the most valuable selected and in the drawer.
    private void ShowTopMissing()
    {
        TabIndex = MissingTab;
        if (IndexHealthVerdict.Top(Missing) is not { } top)
            return;
        SelectedMissing = top;
        ShowRow?.Invoke(top);
    }

    // ── The scripts: shown for review, never run ──────────────────────────

    // Selected rows if the user picked some, otherwise every row the filter shows, in the grid's
    // current sort order so the script reads like what's on screen. Null takes them as read.
    private static List<T> Scripted<T>(IEnumerable? shown, IEnumerable<T> rows, ScriptScope scope)
    {
        var selected = scope.Selected.OfType<T>().ToHashSet();
        return (shown?.OfType<T>() ?? rows).Where(i => selected.Count == 0 || selected.Contains(i)).ToList();
    }

    /// <summary>The CREATE INDEX script for the missing indexes selected, or those shown.</summary>
    [RelayCommand]
    private void CreateScript(IEnumerable? shown)
    {
        var suggestions = Scripted(shown, Missing, MissingScope);
        if (suggestions.Count == 0) return;

        var notice =
            $"Review before running. {suggestions.Count:N0} index(es) will be created. " +
            "SQL Server does not check these suggestions against your existing indexes, and every new index slows writes. SqlVitals does not run this script.";

        var now = _time.GetLocalNow().DateTime;
        dialogs.ShowSqlScript(new SqlScript(
            "Create Missing Indexes",
            notice,
            MissingIndexCreateScript.Build(suggestions, now),
            $"CreateMissingIndexes_{now:yyyyMMdd_HHmm}.sql"), modal: true);
    }

    /// <summary>The DROP INDEX script for the unused indexes selected, or those shown.</summary>
    [RelayCommand]
    private void DropScript(IEnumerable? shown)
    {
        var indexes = Scripted(shown, Unused, UnusedScope);
        if (indexes.Count == 0) return;

        int uniqueCount = indexes.Count(i => i.IsUnique);
        var notice =
            $"Review before running. {indexes.Count - uniqueCount:N0} index(es) will be dropped" +
            (uniqueCount > 0 ? $"; {uniqueCount:N0} unique index(es) are commented out" : "") +
            ". Usage stats reset on every SQL Server restart — confirm the uptime in the script covers a full business cycle. SqlVitals does not run this script.";

        var now = _time.GetLocalNow().DateTime;
        dialogs.ShowSqlScript(new SqlScript(
            "Drop Unused Indexes",
            notice,
            UnusedIndexDropScript.Build(indexes, now),
            $"DropUnusedIndexes_{now:yyyyMMdd_HHmm}.sql"), modal: true);
    }

    /// <summary>The ALTER INDEX script for the fragmented indexes selected, or those shown.</summary>
    [RelayCommand]
    private void MaintenanceScript(IEnumerable? shown)
    {
        if (!TryReadScriptThresholds(out var options, out var error, out var invalid))
        {
            SetFragmentationStatus(error, isError: true);
            InvalidField?.Invoke(invalid);
            return;
        }

        var indexes = Scripted(shown, Fragmentation, FragmentationScope);
        if (indexes.Count == 0) return;

        var summary = IndexMaintenanceScript.Summarize(indexes, options);
        var notice =
            $"Review before running. {summary.Rebuild:N0} index(es) will be rebuilt and {summary.Reorganize:N0} reorganized" +
            (summary.Skipped > 0 ? $"; {summary.Skipped:N0} skipped" : "") +
            (options.OnlineRebuild ? ", rebuilds using ONLINE = ON" : "") +
            ". A rebuild without ONLINE = ON locks the table for the whole operation — run this in a maintenance window. SqlVitals does not run this script.";

        var now = _time.GetLocalNow().DateTime;
        dialogs.ShowSqlScript(new SqlScript(
            "Index Maintenance",
            notice,
            IndexMaintenanceScript.Build(indexes, options, now),
            $"IndexMaintenance_{now:yyyyMMdd_HHmm}.sql"), modal: true);
    }

    // ── Fragmentation ─────────────────────────────────────────────────────

    /// <summary>The fragmented indexes the criteria let through.</summary>
    public GridRows<IndexFragmentation> Fragmentation { get; } = [];

    [ObservableProperty]
    private IndexFragmentation? _selectedFragmentation;

    /// <summary>The boxes: the fragmentation read's criteria, and the maintenance script's thresholds.</summary>
    [ObservableProperty]
    private string _minFragText = "10";

    [ObservableProperty]
    private string _minPagesText = "1000";

    [ObservableProperty]
    private string _reorganizeText = "5";

    [ObservableProperty]
    private string _rebuildText = "30";

    /// <summary>Which box holds what can't be used, for the view to focus: one of the <c>…Field</c> names.</summary>
    public event Action<string>? InvalidField;

    internal const string MinFragField = nameof(MinFragText), MinPagesField = nameof(MinPagesText),
                          ReorganizeField = nameof(ReorganizeText), RebuildField = nameof(RebuildText);

    /// <summary>"12 indexes with ≥ 10% fragmentation and ≥ 1,000 pages · updated 09:41:12", or what's wrong. The loading bar says it is checking (#117).</summary>
    [ObservableProperty]
    private string _fragmentationStatus = string.Empty;

    /// <summary>The status is a problem: in red.</summary>
    [ObservableProperty]
    private bool _fragmentationFailed;

    /// <summary>A read of the fragmentation is running: its Refresh waits.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshFragmentationCommand))]
    private bool _readingFragmentation;

    // ONLINE = ON is an edition feature: offering it where the server rejects it would only produce
    // a script that fails. Asked once per page; a new page is made after a connection switch.
    private bool _onlineChecked;

    /// <summary>The server can rebuild online: the box is offered, and ticked.</summary>
    [ObservableProperty]
    private bool _canRebuildOnline;

    [ObservableProperty]
    private bool _onlineRebuild;

    [ObservableProperty]
    private string _onlineRebuildToolTip = "Checking whether this server's edition supports online index rebuilds…";

    private async Task CheckOnlineRebuildAsync(CancellationToken cancel)
    {
        if (_onlineChecked) return;
        _onlineChecked = true;

        try
        {
            if (await server.SupportsOnlineIndexRebuildAsync(cancel))
            {
                CanRebuildOnline     = true;
                OnlineRebuild        = true;
                OnlineRebuildToolTip = "Rebuild rowstore indexes with WITH (ONLINE = ON) so the table stays available. "
                    + "Slower, and it needs more log and tempdb space.";
            }
            else
            {
                OnlineRebuildToolTip = "This server's edition does not support online index rebuilds — "
                    + "the script would fail. Enterprise, Developer, Azure SQL Database and Managed Instance do.";
            }
        }
        catch
        {
            // The edition check failed (server unreachable, permissions, the page left). Leave the
            // box off: a rebuild without ONLINE runs everywhere, one with it does not.
            _onlineChecked = false;
            OnlineRebuildToolTip = "Could not read the server edition, so online rebuilds are not offered. "
                + "Refresh to try again.";
        }
    }

    /// <summary>Reads the fragmentation again with the criteria in the boxes; Enter in either box does too.</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshFragmentation))]
    private Task RefreshFragmentation()
    {
        if (FragmentationCriteria(report: true) is not { } criteria)
            return Task.CompletedTask;
        return ReadFragmentationOnItsOwnAsync(criteria.MinFrag, criteria.MinPages);
    }

    private bool CanRefreshFragmentation() => !ReadingFragmentation;

    // The page's own read is cancelled when the page is left; a failure is said in the status only.
    // The loading bar says it is checking, and its Stop stops only this read (#117).
    private async Task ReadFragmentationOnItsOwnAsync(double minFrag, long minPages)
    {
        using var reading = ShowReading("Checking fragmentation", "Fragmentation checked", StopFragmentation);
        try
        {
            await ReadFragmentationAsync(minFrag, minPages, CancellationToken.None);
            reading.Done();
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                AppLog.Error("Index Health: reading the fragmentation failed", ex);
        }
    }

    // Cancels the fragmentation read before: another read of it, or leaving the page.
    private CancellationTokenSource? _fragmentationRead;

    // The loading bar's Stop was pressed on the fragmentation read in progress.
    private bool _fragmentationStopped;

    private void StopFragmentation()
    {
        _fragmentationStopped = true;
        _fragmentationRead?.Cancel();
    }

    /// <summary>
    /// Reads only the fragmentation: the other tabs don't depend on these criteria, so they keep
    /// their rows. Says how it went in the status, and throws what failed for the caller.
    /// </summary>
    private async Task ReadFragmentationAsync(double minFrag, long minPages, CancellationToken cancel)
    {
        _fragmentationRead?.Cancel();
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _fragmentationRead    = read;
        _fragmentationStopped = false;
        ReadingFragmentation  = true;
        try
        {
            var rows = (await repo.GetIndexFragmentationAsync(minFrag, minPages, read.Token)).ToList();
            read.Token.ThrowIfCancellationRequested();

            var kept = SelectedFragmentation;
            Fragmentation.Replace(rows);
            SelectedFragmentation = kept is null ? null : rows.FirstOrDefault(f => Key(f) == Key(kept));
            SetFragmentationStatus(
                $"{rows.Count:N0} index{(rows.Count == 1 ? "" : "es")} with ≥ {minFrag:0.##}% fragmentation " +
                $"and ≥ {minPages:N0} pages · updated {_time.GetLocalNow():HH:mm:ss}",
                isError: false);
        }
        catch (OperationCanceledException) when (_fragmentationStopped && ReferenceEquals(_fragmentationRead, read))
        {
            SetFragmentationStatus("Stopped — the grid still shows the previous results.", isError: false);
            throw;
        }
        catch (Exception ex) when (!read.IsCancellationRequested)
        {
            SetFragmentationStatus(ErrorStatus.Format(ex) + " — the grid still shows the previous results.", isError: true);
            throw;
        }
        finally
        {
            if (ReferenceEquals(_fragmentationRead, read))
            {
                _fragmentationRead   = null;
                ReadingFragmentation = false;
            }
        }
    }

    protected override void OnLeaving()
    {
        _fragmentationRead?.Cancel();
    }

    private void SetFragmentationStatus(string text, bool isError)
    {
        FragmentationStatus = text;
        FragmentationFailed = isError;
    }

    // The criteria in the boxes; null when one can't be used, said in the status when asked to.
    private (double MinFrag, long MinPages)? FragmentationCriteria(bool report = false)
    {
        string error, invalid;
        if (!double.TryParse(MinFragText, NumberStyles.Float, CultureInfo.CurrentCulture, out var minFrag) || minFrag <= 0 || minFrag > 100)
            (error, invalid) = ("Min Frag % must be a number greater than 0 and at most 100.", MinFragField);
        else if (!TryReadMinPages(out var minPages))
            (error, invalid) = ("Min Page Count must be a whole number greater than 0.", MinPagesField);
        else
            return (minFrag, minPages);

        if (report)
        {
            SetFragmentationStatus(error, isError: true);
            InvalidField?.Invoke(invalid);
        }
        return null;
    }

    private bool TryReadMinPages(out long minPages) =>
        long.TryParse(MinPagesText, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out minPages) && minPages > 0;

    /// <summary>
    /// Reads the script thresholds. Min page count is shared with the criteria above: an index too
    /// small to be worth defragmenting is the same index in both places.
    /// </summary>
    private bool TryReadScriptThresholds(out IndexMaintenanceOptions options, out string error, out string invalid)
    {
        options = new IndexMaintenanceOptions();
        (error, invalid) = (string.Empty, string.Empty);

        if (!double.TryParse(ReorganizeText, NumberStyles.Float, CultureInfo.CurrentCulture, out double reorg) || reorg <= 0 || reorg > 100)
        {
            (error, invalid) = ("Reorganize from % must be a number greater than 0 and at most 100.", ReorganizeField);
            return false;
        }

        if (!double.TryParse(RebuildText, NumberStyles.Float, CultureInfo.CurrentCulture, out double rebuild) || rebuild <= 0 || rebuild > 100)
        {
            (error, invalid) = ("Rebuild from % must be a number greater than 0 and at most 100.", RebuildField);
            return false;
        }

        if (rebuild < reorg)
        {
            (error, invalid) = ("Rebuild from % must be at least the reorganize threshold — a rebuild is the heavier fix.", RebuildField);
            return false;
        }

        if (!TryReadMinPages(out var minPages))
        {
            (error, invalid) = ("Min Page Count must be a whole number greater than 0.", MinPagesField);
            return false;
        }

        options = new IndexMaintenanceOptions
        {
            ReorganizeThresholdPercent = reorg,
            RebuildThresholdPercent    = rebuild,
            MinPageCount               = minPages,
            OnlineRebuild              = CanRebuildOnline && OnlineRebuild,
        };
        return true;
    }

    /// <summary>
    /// What a script button covers, which the view tells it: the rows selected, how many the grid
    /// shows and whether a search filters them. The button never claims "all" for a filtered grid.
    /// </summary>
    public sealed partial class ScriptScope(string script) : ObservableObject
    {
        /// <summary>The rows selected in the grid; none means every row shown.</summary>
        public IReadOnlyList<object> Selected { get; private set; } = [];

        /// <summary>The grid shows rows: the button can make a script.</summary>
        [ObservableProperty]
        private bool _hasRows;

        /// <summary>"Generate CREATE Script (all)", "(3 selected)", "(12 shown)".</summary>
        [ObservableProperty]
        private string _buttonText = $"Generate {script} Script";

        /// <summary>What the grid shows now.</summary>
        public void Update(IReadOnlyList<object> selected, int shown, bool filtered)
        {
            Selected   = selected;
            HasRows    = shown > 0;
            ButtonText = $"Generate {script} Script ({(selected.Count > 0 ? $"{selected.Count} selected" : filtered ? $"{shown} shown" : "all")})";
        }
    }
}
