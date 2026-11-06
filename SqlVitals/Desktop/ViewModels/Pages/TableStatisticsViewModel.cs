using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.TableStatistics;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Table Statistics (#242): a database's tables, the largest first, and the selected one's indexes
/// with their sizes and usage. Reads when opened and on Refresh, never on a timer. Choosing another
/// database or table cancels what's being read for the last choice, so its answer can't appear for
/// the new one. The filters here are those the box can't hold: the view applies them through
/// <see cref="ShowsTable"/> and <see cref="ShowsIndex"/>, so Copy and export follow the rows shown.
/// </summary>
public sealed partial class TableStatisticsViewModel : PageViewModel
{
    private readonly ITableStatisticsRepository _repo;
    private readonly INavigator _navigator;

    // The answers being waited for: cancelled by the next choice, and by leaving (through the page's read or lifetime).
    private CancellationTokenSource? _tablesRun;
    private CancellationTokenSource? _detailsRun;

    // The view model is choosing, not the user: nothing is read because of it.
    private bool _holding;

    // The rows in the grid are the chosen database's: until then, an empty grid isn't "no tables".
    private bool _tablesRead;

    // No tables were listed, and the login may not see them all: it can't say there are none.
    private bool _noneVisible;

    // The table to select again on the first read, which the filter may hide from the grid's own memory.
    private int? _restoredTable;

    public TableStatisticsViewModel(ITableStatisticsRepository repo, INavigator navigator)
    {
        _repo = repo;
        _navigator = navigator;
        Schema.PropertyChanged += (_, e) => Follow(e, TableFiltersChanged);
        IndexType.PropertyChanged += (_, e) => Follow(e, IndexFiltersChanged);
    }

    private static void Follow(PropertyChangedEventArgs e, Action? changed)
    {
        if (e.PropertyName == nameof(MultiFilterChoices.Values))
            changed?.Invoke();
    }

    // ── The database ──────────────────────────────────────────────────────

    /// <summary>The databases offered.</summary>
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>The database shown; the connection's until another is chosen. Null until the first read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TablesHeader))]
    private string? _selectedDatabase;

    /// <summary>The dropdown shows: false on Azure SQL Database, where the page reads the connected database.</summary>
    [ObservableProperty]
    private bool _canSwitch;

    /// <summary>The database the connection points to.</summary>
    [ObservableProperty]
    private string? _connectedDatabase;

    public string TablesHeader => SelectedDatabase is { } database ? $"Tables in {database}, largest first" : "Tables, largest first";

    // Null reads the connection's own database, the only way on Azure SQL Database.
    private string? DatabaseRead => CanSwitch ? SelectedDatabase : null;

    partial void OnSelectedDatabaseChanged(string? value)
    {
        OnPropertyChanged(nameof(IndexHealthNote));
        if (_holding || value is null)
            return;

        // What's on screen is about the last database: it goes now, whatever is still being read.
        _tablesRun?.Cancel();
        ForgetDatabase();
        ReportContextChanged();
        _ = ReportErrorsOf(RereadAsync());
    }

    // Nothing of the last database stays: its rows, its schemas, or an object id, which means nothing in another.
    private void ForgetDatabase()
    {
        var holding = _holding;
        ShowNoTable();
        _restoredTable = null;
        _tablesRead = false;
        _noneVisible = false;
        _holding = true;
        Tables.Replace([]);
        Schema.Clear();
        Schema.Replace([]);
        _holding = holding;
        Verdict = null;
    }

    // ── The tables ────────────────────────────────────────────────────────

    /// <summary>The database's tables, as read; the grid's view filters them.</summary>
    public GridRows<TableSizeRow> Tables { get; } = [];

    /// <summary>The schemas ticked: a filter on the table grid.</summary>
    public MultiFilterChoices Schema { get; } = new();

    /// <summary>A filter on the tables changed: the view applies <see cref="ShowsTable"/> again.</summary>
    public event Action? TableFiltersChanged;

    /// <summary>The filter box above the tables is to be emptied.</summary>
    public event Action? TableSearchCleared;

    /// <summary>A table to scroll into view, past any filter: the verdict's Show it selected it.</summary>
    public event Action<TableSizeRow>? ShowRow;

    public bool HasTableFilters => Schema.Values is not null;

    public bool ShowsTable(object row) => row is TableSizeRow table && Schema.Matches(table.SchemaName);

    /// <summary>Why the table grid is empty; null when it shows rows.</summary>
    [ObservableProperty]
    private string? _noTablesText;

    /// <summary>How many rows the table grid shows now, after every filter: the view tells it.</summary>
    public void TablesShown(int shown)
    {
        NoTablesText = shown > 0 || !_tablesRead ? null
            : Tables.Count > 0 ? "No tables match the filters."
            : _noneVisible ? "This login can't see any tables in this database."
            : "This database has no user tables.";
    }

    /// <summary>Every filter on the table grid off. The table selected stays selected.</summary>
    [RelayCommand]
    private void ClearTableFilters()
    {
        Schema.Clear();
        TableSearchCleared?.Invoke();
    }

    // ── The selected table ────────────────────────────────────────────────

    /// <summary>
    /// The table whose details show. The view sets it from the grid only when a row is chosen, so a
    /// filter that hides the row, which clears the grid's selection, doesn't change it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedText))]
    private TableSizeRow? _selectedTable;

    public bool HasSelection => SelectedTable is not null;

    public string SelectedText => SelectedTable is { } row ? $"Selected {row.Table.FullName}" : "";

    partial void OnSelectedTableChanged(TableSizeRow? value)
    {
        if (_holding)
            return;
        if (value is null)
            ShowNoTable();
        else
            _ = ReportErrorsOf(ReadChosenTableAsync(value));
    }

    /// <summary>Its indexes, as read; the grid's view filters them.</summary>
    public GridRows<TableIndexRow> Indexes { get; } = [];

    /// <summary>The whole table's space, whatever the index grid's filters hide.</summary>
    [ObservableProperty]
    private IReadOnlyList<SpaceBarPart> _spaceBar = [];

    [ObservableProperty]
    private string _dataText = TableSpace.Unknown;

    [ObservableProperty]
    private string _indexesText = TableSpace.Unknown;

    [ObservableProperty]
    private string _totalText = TableSpace.Unknown;

    [ObservableProperty]
    private string _rowsText = TableSpace.Unknown;

    /// <summary>What couldn't be read of this table, or that it's locked; null when all of it was.</summary>
    [ObservableProperty]
    private string? _detailProblem;

    /// <summary>Since when the reads and writes count.</summary>
    [ObservableProperty]
    private string _usageNote = "";

    /// <summary>Index Health reads the connection's database: said when another one is shown here.</summary>
    public string? IndexHealthNote =>
        CanSwitch && SelectedDatabase is { } shown && ConnectedDatabase is { } connected && shown != connected
            ? $"Index Health reads the connection's database, {connected}, not {shown}."
            : null;

    /// <summary>The index types ticked: a filter on the index grid.</summary>
    public MultiFilterChoices IndexType { get; } = new();

    /// <summary>Only the indexes flagged as costing more than they give.</summary>
    [ObservableProperty]
    private bool _flaggedOnly;

    partial void OnFlaggedOnlyChanged(bool value) => IndexFiltersChanged?.Invoke();

    /// <summary>A filter on the indexes changed: the view applies <see cref="ShowsIndex"/> again.</summary>
    public event Action? IndexFiltersChanged;

    /// <summary>The filter box above the indexes is to be emptied.</summary>
    public event Action? IndexSearchCleared;

    public bool HasIndexFilters => IndexType.Values is not null || FlaggedOnly;

    public bool ShowsIndex(object row) =>
        row is TableIndexRow index && IndexType.Matches(index.Type) && (!FlaggedOnly || index.IsCostly);

    /// <summary>Why the index grid is empty; null when it shows rows.</summary>
    [ObservableProperty]
    private string? _noIndexesText;

    /// <summary>How many rows the index grid shows now, after every filter: the view tells it.</summary>
    public void IndexesShown(int shown) =>
        NoIndexesText = shown > 0 || Indexes.Count == 0 ? null : "No indexes match the filters.";

    /// <summary>Every filter on the index grid off.</summary>
    [RelayCommand]
    private void ClearIndexFilters()
    {
        IndexType.Clear();
        FlaggedOnly = false;
        IndexSearchCleared?.Invoke();
    }

    /// <summary>A flagged index's link.</summary>
    [RelayCommand]
    private Task OpenIndexHealth() => ReportErrorsOf(_navigator.GoToAsync("IndexHealth"));

    // ── Reading ───────────────────────────────────────────────────────────

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _tablesRun = run;
        try
        {
            ShowDatabases(await _repo.GetTableDatabasesAsync(run.Token));
            var list = await _repo.GetTableSizesAsync(DatabaseRead, run.Token);
            run.Token.ThrowIfCancellationRequested();
            await ShowTablesAsync(list, run.Token);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            // Another database was chosen: its read follows this one, and says what there is.
        }
        finally
        {
            if (ReferenceEquals(_tablesRun, run))
                _tablesRun = null;
        }
    }

    protected override void ShowLoadError(Exception exception) => ShowError($"the tables in {SelectedDatabase ?? "this database"}", exception);

    protected override void OnLeaving()
    {
        _detailsRun?.Cancel();
    }

    private void ShowDatabases(TableDatabases databases)
    {
        _holding = true;
        try
        {
            ConnectedDatabase = databases.Connected;
            CanSwitch = databases.CanSwitch;
            var wanted = SelectedDatabase is { } kept && databases.Names.Contains(kept) ? kept : databases.Connected;
            if (SelectedDatabase is not null && wanted != SelectedDatabase)
                ForgetDatabase();
            if (!Databases.SequenceEqual(databases.Names))
            {
                Databases.Clear();
                foreach (var name in databases.Names)
                    Databases.Add(name);
            }
            SelectedDatabase = wanted;
        }
        finally
        {
            _holding = false;
        }
        OnPropertyChanged(nameof(IndexHealthNote));
    }

    private async Task ShowTablesAsync(TableSizeList list, CancellationToken cancel)
    {
        var database = SelectedDatabase ?? ConnectedDatabase ?? "";
        Verdict = TableStatisticsVerdict.From(database, list.Tables, list.Failures);

        var largest = list.Tables.Max(t => t.TotalPages) ?? 0;
        var rows = list.Tables.Select(t => new TableSizeRow(t, largest)).ToList();

        // The grid clears its selection as the rows go: the choice is noted first, and made again.
        var selected = _restoredTable ?? SelectedTable?.Table.ObjectId;
        _restoredTable = null;
        _holding = true;
        TableSizeRow? again;
        try
        {
            _tablesRead = true;
            _noneVisible = list.Failures.Any(f => f.What == TableReadFailure.Names);
            Schema.Replace(rows.Select(r => r.SchemaName));
            Tables.Replace(rows);
            again = rows.FirstOrDefault(r => r.Table.ObjectId == selected);
            SelectedTable = again;
        }
        finally
        {
            _holding = false;
        }

        if (again is null)
            ShowNoTable();
        else
            await ReadDetailsAsync(again, cancel);
    }

    // The user chose a table: the loading bar shows its read, and its Stop ends it.
    private async Task ReadChosenTableAsync(TableSizeRow row)
    {
        using var reading = ShowReading("Reading the table's indexes", "Indexes read", () => _detailsRun?.Cancel());
        if (await ReadDetailsAsync(row, CancellationToken.None))
            reading.Done();
    }

    // True when it read: not when another choice cancelled it, or it failed.
    private async Task<bool> ReadDetailsAsync(TableSizeRow row, CancellationToken cancel)
    {
        _detailsRun?.Cancel();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _detailsRun = run;
        ShowFigures(row.Table);
        try
        {
            var list = await _repo.GetTableIndexesAsync(DatabaseRead, row.Table.ObjectId, run.Token);
            if (run.IsCancellationRequested)
            {
                ShowStopped(row, run);
                return false;
            }
            ShowDetails(row.Table, list);
            return true;
        }
        catch (Exception ex) when (run.IsCancellationRequested || ex is OperationCanceledException)
        {
            // Another choice was made, the page left, or the loading bar's Stop was pressed.
            ShowStopped(row, run);
            return false;
        }
        catch (Exception ex)
        {
            Clear(row.Table);
            DetailProblem = ErrorStatus.Format(ex) + (ErrorStatus.Fix(ex) is { } fix ? $"\nA DBA can grant it once:\n{fix}" : "");
            ReportSideError("Table Statistics indexes", ex);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_detailsRun, run))
                _detailsRun = null;
        }
    }

    // Cancelled with no other choice made: the loading bar's Stop. Its grid is empty, and this says why.
    private void ShowStopped(TableSizeRow row, CancellationTokenSource run)
    {
        if (ReferenceEquals(_detailsRun, run) && ReferenceEquals(SelectedTable, row))
            DetailProblem = "Not read: it was stopped before it finished. Refresh (F5) to read again.";
    }

    // A table was chosen: what the list knows of it shows now, and last table's indexes go.
    private void ShowFigures(TableSize table)
    {
        Clear(table);
        DataText    = TableSpace.SizeText(table.DataPages);
        IndexesText = TableSpace.SizeText(table.IndexPages);
        TotalText   = table.IsLocked ? "Locked" : TableSpace.SizeText(table.TotalPages);
        RowsText    = Count(table.RowsInTable);
    }

    private void Clear(TableSize? table)
    {
        Indexes.Replace([]);
        SpaceBar = [];
        DetailProblem = null;
        UsageNote = "";
        NoIndexesText = null;
        if (table is null)
            DataText = IndexesText = TotalText = RowsText = TableSpace.Unknown;
    }

    private void ShowNoTable()
    {
        _detailsRun?.Cancel();
        var holding = _holding;
        _holding = true;
        SelectedTable = null;
        _holding = holding;
        Clear(null);
    }

    private void ShowDetails(TableSize table, TableIndexList list)
    {
        // A table always has a row in sys.indexes, its heap at least: none, and it has gone since the list was read.
        if (list.Indexes.Count == 0)
        {
            DetailProblem = $"{table.FullName} is no longer in this database. Refresh reads the list again.";
            return;
        }

        var known = list.Indexes.All(i => i.ReservedPages is not null);
        var data = known ? list.Indexes.Where(i => i.IsData).Sum(i => i.ReservedPages!.Value) : (long?)null;
        var other = known ? list.Indexes.Where(i => !i.IsData).Sum(i => i.ReservedPages!.Value) : (long?)null;
        var total = data + other;

        IndexType.Replace(list.Indexes.Select(i => TableSpace.TypeText(i.Type)));
        Indexes.Replace(list.Indexes.Select(i => new TableIndexRow(i, total ?? 0)).ToList());
        SpaceBar = total > 0 ? Parts(list.Indexes, total.Value) : [];

        if (known)
        {
            DataText    = TableSpace.SizeText(data);
            IndexesText = TableSpace.SizeText(other);
            TotalText   = TableSpace.SizeText(total);
            if (list.Indexes.FirstOrDefault(i => i.IsData)?.RowsInIndex is { } rows)
                RowsText = Count(rows);
        }
        else if (list.IsLocked)
        {
            DataText = IndexesText = RowsText = TableSpace.Unknown;
            TotalText = "Locked";
        }

        var problems = list.Failures.Select(TableStatisticsVerdict.Describe).ToList();
        if (list.IsLocked)
            problems.Insert(0, $"{table.FullName} is under a schema change, so its sizes can't be read now. Refresh reads them again.");
        DetailProblem = problems.Count == 0 ? null : string.Join(' ', problems);

        UsageNote = list.ServerStarted is { } started
            ? string.Format(CultureInfo.CurrentCulture, "Reads and writes count since the server restarted on {0:yyyy-MM-dd HH:mm}, and start again from 0 when it next does. A clustered index is the table's data, so it counts as data.", started)
            : "Reads and writes count since the server last restarted. A clustered index is the table's data, so it counts as data.";
    }

    private static List<SpaceBarPart> Parts(IReadOnlyList<TableIndexSize> indexes, long total) =>
        indexes.Where(i => i.ReservedPages > 0)
               .OrderBy(i => i.IsData ? 0 : 1)
               .ThenByDescending(i => i.ReservedPages)
               .Select(i =>
               {
                   var share = (double)i.ReservedPages!.Value / total;
                   var name = i.IsData ? $"Data ({i.Name ?? "heap"})" : i.Name;
                   return new SpaceBarPart(share,
                       string.Format(CultureInfo.CurrentCulture, "{0}: {1} ({2:0.0} %)", name, TableSpace.SizeText(i.ReservedPages), share * 100),
                       i.IsData ? SpaceBarPart.Kinds.Data : TableSpace.IsCostly(i) ? SpaceBarPart.Kinds.Costly : SpaceBarPart.Kinds.Index);
               })
               .ToList();

    private static string Count(long? value) =>
        value is { } known ? known.ToString("N0", CultureInfo.CurrentCulture) : TableSpace.Unknown;

    // The verdict's Show it: the largest table, past any filter that hides it.
    protected override void OnAction(VerdictAction action)
    {
        if (action.Key != TableStatisticsVerdict.ShowLargest)
            return;
        var largest = TableStatisticsVerdict.Largest(Tables.Select(t => t.Table));
        if (Tables.FirstOrDefault(t => ReferenceEquals(t.Table, largest)) is not { } row)
            return;
        SelectedTable = row;
        ShowRow?.Invoke(row);
    }

    // ── Coming back ───────────────────────────────────────────────────────

    private sealed record Saved(string? Database, IReadOnlyList<string> Schemas, IReadOnlyList<string> Types, bool Flagged, int? Table);

    private static List<string> Ticked(MultiFilterChoices box) => box.Items.Where(i => i.IsChecked).Select(i => i.Value).ToList();

    private static void Tick(MultiFilterChoices box, IReadOnlyList<string> values)
    {
        box.Replace(values);
        foreach (var item in box.Items)
            item.IsChecked = true;
    }

    /// <summary>The database, the filters the view can't keep, and the table selected: the filter may hide it from the grid.</summary>
    public override object? SaveState() =>
        new Saved(SelectedDatabase, Ticked(Schema), Ticked(IndexType), FlaggedOnly, SelectedTable?.Table.ObjectId);

    public override void RestoreState(object state)
    {
        if (state is not Saved saved)
            return;
        _holding = true;
        try
        {
            SelectedDatabase = saved.Database;
            _restoredTable = saved.Table;
        }
        finally
        {
            _holding = false;
        }
        Tick(Schema, saved.Schemas);
        Tick(IndexType, saved.Types);
        FlaggedOnly = saved.Flagged;
    }
}
