using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Scripting;
using SqlVitals.Engine.TableStatistics;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// Table Statistics' Statistics tab (#243): the selected table's statistics objects and what the
/// chosen one holds. It reads when the tab is shown or a table is chosen while it is, never for
/// someone staying on Sizes, and a new choice cancels the last one's answer.
/// </summary>
/// <remarks>
/// Privacy: a histogram key is a real value from the table (#243, #175). Keys reach the chart and the
/// grid's rows only. Nothing here logs, reports or saves one: the messages are built from names and
/// counts, the findings hold step numbers, and what's kept on leaving is the object's name.
/// </remarks>
public sealed partial class TableStatisticsDetailsViewModel : ObservableObject
{
    public const string AllSteps = "All steps";
    public const string FarSteps = "Keys 10x or more from the unknown-value estimate";
    public const string AboveAverageSteps = "Above-average AVG_RANGE_ROWS";

    private const string Stopped = "Not read: it was stopped before it finished. Refresh (F5) to read again.";

    private readonly ITableStatisticsRepository _repo;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly Func<string, string, Action, IReading> _showReading;
    private readonly Action<string, Exception> _reportError;

    private TableSize? _table;
    private string? _database;
    private string _databaseName = "";

    // What's on screen is what's there: nothing to read when the tab is shown again.
    private bool _read;

    // The answer being waited for: cancelled by the next choice, and by leaving.
    private CancellationTokenSource? _run;

    // The view model is choosing the object, not the user: nothing is read because of it.
    private bool _holding;

    // The object to choose again on the first read after coming back.
    private string? _restoredName;

    // The object a link asked for: to say so if the table doesn't have it.
    private string? _wantedName;
    private string? _missingObject;

    /// <param name="showReading">The loading bar: what it says, what it says when done, and what its Stop does.</param>
    /// <param name="reportError">A failed read beside the page's own: the top bar and the log.</param>
    public TableStatisticsDetailsViewModel(ITableStatisticsRepository repo, IDialogService dialogs, TimeProvider time,
                                           Func<string, string, Action, IReading> showReading, Action<string, Exception> reportError)
    {
        _repo = repo;
        _dialogs = dialogs;
        _time = time;
        _showReading = showReading;
        _reportError = reportError;
    }

    // ── What's chosen ─────────────────────────────────────────────────────

    /// <summary>The Statistics tab is on screen.</summary>
    public bool IsShowing { get; set; }

    /// <summary>Whether the tab, being shown, has something to read.</summary>
    public bool NeedsRead => IsShowing && _table is not null && !_read;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTable), nameof(SelectedText))]
    private TableSize? _tableShown;

    public bool HasTable => TableShown is not null;

    public string SelectedText => TableShown is { } table ? $"Selected {table.FullName}" : "";

    /// <summary>The table's statistics objects, the clustered index's first.</summary>
    public ObservableCollection<StatisticsObject> Objects { get; } = [];

    /// <summary>The object shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindText))]
    [NotifyCanExecuteChangedFor(nameof(GenerateScriptCommand))]
    private StatisticsObject? _selectedObject;

    /// <summary>Index or column statistics, and the column its histogram is on.</summary>
    public string KindText => SelectedObject is not { } chosen ? ""
        : (chosen.IsColumnstore ? "Columnstore index" : chosen.IsClustered ? "Clustered index" : chosen.IsIndex ? "Index" : "Column") + " statistics"
          + (chosen.IsFiltered ? ", filtered" : "")
          + (chosen.LeadingColumn is { } column ? $", histogram on {column}" : "");

    partial void OnSelectedObjectChanged(StatisticsObject? value)
    {
        if (!_holding && value is not null)
        {
            _missingObject = null;
            _ = ReadChosenObjectAsync(value);
        }
    }

    /// <summary>A link asks for this object, on the table about to be chosen (#244).</summary>
    public void Want(string statisticsName)
    {
        _restoredName = statisticsName;
        _wantedName = statisticsName;
    }

    /// <summary>
    /// The login isn't allowed to read the chosen object (#244): its header, findings, chart and script
    /// are off, and <see cref="Problem"/> says what would allow it and what that also allows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRead))]
    [NotifyCanExecuteChangedFor(nameof(GenerateScriptCommand))]
    private bool _readDenied;

    public bool CanRead => !ReadDenied;

    // ── What it holds ─────────────────────────────────────────────────────

    [ObservableProperty]
    private string _lastUpdatedText = TableSpace.Unknown;

    [ObservableProperty]
    private string _rowsText = TableSpace.Unknown;

    [ObservableProperty]
    private string _rowsSampledText = TableSpace.Unknown;

    [ObservableProperty]
    private string _sampleRateText = TableSpace.Unknown;

    [ObservableProperty]
    private string _stepsText = TableSpace.Unknown;

    [ObservableProperty]
    private string _modificationsText = TableSpace.Unknown;

    /// <summary>The Good, Fair or Poor line; null with nothing read.</summary>
    [ObservableProperty]
    private PageVerdict? _objectVerdict;

    /// <summary>What to look at, each with what to try. Always from the whole object.</summary>
    [ObservableProperty]
    private IReadOnlyList<StatisticsFinding> _findings = [];

    /// <summary>The chart's steps: the whole object, whatever the grid's filters hide.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistogram))]
    private IReadOnlyList<HistogramStep> _chartSteps = [];

    public bool HasHistogram => ChartSteps.Count > 0;

    /// <summary>The chart's reference line.</summary>
    [ObservableProperty]
    private double? _unknownEstimate;

    [ObservableProperty]
    private bool _logScale;

    /// <summary>Shown in place of the chart when the object has no histogram; null otherwise.</summary>
    [ObservableProperty]
    private string? _noHistogramText;

    /// <summary>What couldn't be read, or that the table is locked; null when all of it was.</summary>
    [ObservableProperty]
    private string? _problem;

    public GridRows<HistogramStepRow> Histogram { get; } = [];

    public GridRows<StatisticsDensityRow> Density { get; } = [];

    [ObservableProperty]
    private bool _hasDensity;

    // ── The grids' filters ────────────────────────────────────────────────

    public IReadOnlyList<string> StepChoices { get; } = [AllSteps, FarSteps, AboveAverageSteps];

    /// <summary>Which steps the histogram grid shows. The chart and findings don't follow it.</summary>
    [ObservableProperty]
    private string _stepChoice = AllSteps;

    partial void OnStepChoiceChanged(string value) => StepFiltersChanged?.Invoke();

    /// <summary>The choice of steps changed: the view applies <see cref="ShowsStep"/> again.</summary>
    public event Action? StepFiltersChanged;

    public bool HasStepFilters => StepChoice != AllSteps;

    public bool ShowsStep(object row) => row is HistogramStepRow step && StepChoice switch
    {
        FarSteps => step.IsFarFromUnknown,
        AboveAverageSteps => step.IsAboveAverageRange,
        _ => true,
    };

    /// <summary>Why the histogram grid is empty; null when it shows rows.</summary>
    [ObservableProperty]
    private string? _noStepsText;

    /// <summary>How many rows the histogram grid shows now, after every filter: the view tells it.</summary>
    public void StepsShown(int shown) =>
        NoStepsText = shown > 0 || Histogram.Count == 0 ? null : "No rows match the filters.";

    /// <summary>Why the density grid is empty; null when it shows rows.</summary>
    [ObservableProperty]
    private string? _noDensityText;

    /// <summary>How many rows the density grid shows now, after its filter: the view tells it.</summary>
    public void DensityShown(int shown) =>
        NoDensityText = shown > 0 || Density.Count == 0 ? null : "No rows match the filter.";

    // ── The script ────────────────────────────────────────────────────────

    /// <summary>The script reads every row (WITH FULLSCAN) rather than SQL Server's sample.</summary>
    [ObservableProperty]
    private bool _fullScan;

    private StatisticsProperties? _properties;

    /// <summary>Shows UPDATE STATISTICS for the chosen object, for review: nothing is run.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateScript))]
    private void GenerateScript()
    {
        if (_table is not { } table || SelectedObject is not { } chosen)
            return;

        var now = _time.GetLocalNow().DateTime;
        var statistic = new StaleStatistic(
            table.SchemaName, table.TableName, chosen.Name,
            _properties?.LastUpdated,
            _properties?.LastUpdated is { } updated ? (int)(now - updated).TotalDays : null,
            _properties?.Rows ?? table.RowsInTable,
            _properties?.RowsSampled,
            _properties?.SamplePct ?? 0,
            _properties?.Modifications ?? 0,
            IsIncremental: false, AutoCreated: false, UserCreated: false,
            _databaseName, now);

        var sampling = FullScan ? StatisticsSampling.FullScan : StatisticsSampling.Default;
        var notice = $"Review before running. {chosen.Name} on {table.FullName} will be updated " +
                     (FullScan ? "WITH FULLSCAN, reading every row: run it off-peak." : "with default sampling.") +
                     " Plans that use it recompile on their next run. SqlVitals does not run this script.";
        _dialogs.ShowSqlScript(new SqlScript(
            "Update Statistics",
            notice,
            UpdateStatisticsScript.Build([statistic], sampling, now),
            string.Create(CultureInfo.InvariantCulture, $"UpdateStatistics_{now:yyyyMMdd_HHmm}.sql")), modal: true);
    }

    // UPDATE STATISTICS fails on a columnstore index.
    private bool CanGenerateScript() => SelectedObject is { IsColumnstore: false } && !ReadDenied;

    // ── Choosing and reading ──────────────────────────────────────────────

    /// <summary>
    /// The table shown, after a choice or a Refresh. What's on screen goes for another table and stays
    /// for the same one, until the read replaces it, so a Refresh doesn't make the page jump.
    /// </summary>
    /// <param name="database">The database to read; null for the connection's.</param>
    /// <param name="databaseName">Its name, for the script's USE.</param>
    public void ChooseTable(TableSize? table, string? database, string databaseName)
    {
        var same = table is not null && _table?.ObjectId == table.ObjectId && _databaseName == databaseName;
        Cancel();
        _table = table;
        _database = database;
        _databaseName = databaseName;
        _read = false;
        TableShown = table;
        if (!same)
        {
            _missingObject = null;
            ClearObjects();
        }
    }

    /// <summary>Stops what's being read: another choice, leaving, or the loading bar's Stop.</summary>
    public void Cancel() => _run?.Cancel();

    /// <summary>Reads if the tab is on screen. True unless it was stopped or failed.</summary>
    public Task<bool> ReadIfShowingAsync(CancellationToken cancel) =>
        IsShowing && _table is not null ? ReadAsync(cancel) : Task.FromResult(true);

    /// <summary>The tab was opened: the loading bar shows the read.</summary>
    public async Task ReadWithBarAsync()
    {
        using var reading = _showReading("Reading the table's statistics", "Statistics read", Cancel);
        if (await ReadAsync(CancellationToken.None))
            reading.Done();
    }

    // The list, then the object it starts on.
    private async Task<bool> ReadAsync(CancellationToken cancel)
    {
        if (_table is not { } table)
            return true;
        _run?.Cancel();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _run = run;
        try
        {
            var list = await _repo.GetTableStatisticsAsync(_database, table.ObjectId, run.Token);
            if (run.IsCancellationRequested)
                return ShowStopped(table, run);

            ShowList(table, list);
            if (SelectedObject is { } chosen)
            {
                var details = await _repo.GetStatisticsDetailsAsync(_database, table.ObjectId, chosen.StatsId, run.Token);
                if (run.IsCancellationRequested)
                    return ShowStopped(table, run);
                ShowDetails(table, chosen, details);
            }
            _read = true;
            return true;
        }
        catch (Exception ex) when (run.IsCancellationRequested || ex is OperationCanceledException)
        {
            return ShowStopped(table, run);
        }
        catch (Exception ex)
        {
            ShowFailed(ex);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_run, run))
                _run = null;
        }
    }

    // The user chose another object: the loading bar shows its read, and its Stop ends it.
    private async Task ReadChosenObjectAsync(StatisticsObject chosen)
    {
        if (_table is not { } table)
            return;
        _run?.Cancel();
        using var run = new CancellationTokenSource();
        _run = run;
        ClearDetails();
        using var reading = _showReading("Reading the statistics", "Statistics read", Cancel);
        try
        {
            var details = await _repo.GetStatisticsDetailsAsync(_database, table.ObjectId, chosen.StatsId, run.Token);
            if (run.IsCancellationRequested)
            {
                ShowStopped(table, run);
                return;
            }
            ShowDetails(table, chosen, details);
            _read = true;
            reading.Done();
        }
        catch (Exception ex) when (run.IsCancellationRequested || ex is OperationCanceledException)
        {
            ShowStopped(table, run);
        }
        catch (Exception ex)
        {
            ShowFailed(ex);
        }
        finally
        {
            if (ReferenceEquals(_run, run))
                _run = null;
        }
    }

    // Cancelled with no other choice made: the loading bar's Stop. What's on screen stays, and this says why.
    private bool ShowStopped(TableSize table, CancellationTokenSource run)
    {
        if (ReferenceEquals(_run, run) && ReferenceEquals(_table, table))
            Problem = Stopped;
        return false;
    }

    private void ShowFailed(Exception exception)
    {
        _read = true;
        ClearDetails();
        Problem = ErrorStatus.Format(exception) + (ErrorStatus.Fix(exception) is { } fix ? $"\nA DBA can grant it once:\n{fix}" : "");
        _reportError("Table Statistics statistics", exception);
    }

    // True when the object chosen before is still there: it stays, whatever it holds.
    private void ShowList(TableSize table, TableStatisticsList list)
    {
        var before = SelectedObject;
        var wanted = _restoredName ?? before?.Name;
        _restoredName = null;
        if (_wantedName is { } asked)
        {
            _missingObject = list.Statistics.Any(s => string.Equals(s.Name, asked, StringComparison.OrdinalIgnoreCase)) ? null
                : $"{table.FullName} has no statistics object named {asked}: it may have been dropped or renamed.";
            _wantedName = null;
        }

        _holding = true;
        try
        {
            if (!Objects.SequenceEqual(list.Statistics))
            {
                Objects.Clear();
                foreach (var statistic in list.Statistics)
                    Objects.Add(statistic);
            }
            SelectedObject = list.Statistics.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
                          ?? list.Statistics.FirstOrDefault(s => s.IsClustered && !s.IsColumnstore)
                          ?? list.Statistics.FirstOrDefault(s => !s.IsColumnstore)
                          ?? (list.Statistics.Count > 0 ? list.Statistics[0] : null);
        }
        finally
        {
            _holding = false;
        }

        // The same object, on a Refresh: what shows stays until its new answer, so the page doesn't jump.
        if (SelectedObject is null || SelectedObject.StatsId != before?.StatsId)
            ClearDetails();
        var problems = Problems(table, list.IsLocked, []);
        Problem = problems;
        if (list.Statistics.Count == 0 && problems is null)
            NoHistogramText = "This table has no statistics objects yet: nothing has needed one, and it has no index.";
    }

    private void ShowDetails(TableSize table, StatisticsObject chosen, StatisticsDetails details)
    {
        _properties = details.Properties;
        ShowHeader(details.Properties);

        var couldRead = !details.IsLocked && details.Failures.Count == 0;
        ReadDenied = details.Properties is null && details.Histogram.Count == 0
                  && details.Failures.Any(f => f.What == TableReadFailure.Statistics);
        var estimate = StatisticsFindings.UnknownEstimate(details);

        UnknownEstimate = estimate;
        ChartSteps = details.Histogram;
        var averageRange = StatisticsFindings.AverageRange(details.Histogram);
        Histogram.Replace(details.Histogram.Select(s => new HistogramStepRow(s, estimate, averageRange)).ToList());
        Density.Replace(details.Density.Select(d => new StatisticsDensityRow(d, details.Properties?.Rows)).ToList());
        HasDensity = Density.Count > 0;

        NoHistogramText = details.Histogram.Count == 0 && couldRead
            ? "No histogram yet: these statistics were never built, or the table is empty. A columnstore index has none."
            : null;

        var assessment = StatisticsFindings.Assess(details);
        Findings = assessment.Findings;
        // Nothing to rate because it couldn't be read says so in Problem, not "nothing to rate yet".
        ObjectVerdict = assessment.Rating is null && !couldRead ? null : StatisticsFindings.Verdict(chosen.Name, assessment);
        Problem = Problems(table, details.IsLocked, details.Failures);
        StepFiltersChanged?.Invoke();
    }

    private string? Problems(TableSize table, bool locked, IReadOnlyList<TableReadFailure> failures)
    {
        var problems = failures.Select(TableStatisticsVerdict.Describe).ToList();
        if (_missingObject is { } missing)
            problems.Insert(0, missing);
        if (locked)
            problems.Insert(0, $"{table.FullName} is under a schema change, so its statistics can't be read now. Refresh reads them again.");
        return problems.Count == 0 ? null : string.Join(' ', problems);
    }

    private void ShowHeader(StatisticsProperties? properties)
    {
        var culture = CultureInfo.CurrentCulture;
        string Count(long? value) => value is { } known ? known.ToString("N0", culture) : TableSpace.Unknown;

        LastUpdatedText = properties?.LastUpdated is { } updated ? updated.ToString("yyyy-MM-dd HH:mm", culture) : TableSpace.Unknown;
        RowsText = Count(properties?.Rows);
        RowsSampledText = Count(properties?.RowsSampled);
        SampleRateText = properties?.SamplePct is { } sample ? sample.ToString("0.##", culture) + " %" : TableSpace.Unknown;
        StepsText = Count(properties?.Steps);
        ModificationsText = Count(properties?.Modifications);
    }

    private void ClearObjects()
    {
        _holding = true;
        try
        {
            Objects.Clear();
            SelectedObject = null;
        }
        finally
        {
            _holding = false;
        }
        ClearDetails();
    }

    private void ClearDetails()
    {
        _properties = null;
        ReadDenied = false;
        ShowHeader(null);
        ObjectVerdict = null;
        Findings = [];
        ChartSteps = [];
        UnknownEstimate = null;
        NoHistogramText = null;
        NoStepsText = null;
        NoDensityText = null;
        Problem = null;
        Histogram.Replace([]);
        Density.Replace([]);
        HasDensity = false;
    }

    // ── Coming back ───────────────────────────────────────────────────────

    /// <summary>The name of the object chosen, to choose again; a name, never a key.</summary>
    public string? ChosenName => SelectedObject?.Name ?? _restoredName;

    /// <summary>Puts back what was kept: the object, the choice of steps and the scale.</summary>
    public void Restore(string? chosenName, string stepChoice, bool logScale, bool fullScan)
    {
        _restoredName = chosenName;
        StepChoice = stepChoice;
        LogScale = logScale;
        FullScan = fullScan;
    }
}
