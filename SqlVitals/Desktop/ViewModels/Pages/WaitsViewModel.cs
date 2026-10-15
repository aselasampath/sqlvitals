using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore.Defaults;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Waits (#178, #188; Top Waits and Wait Trend before): the trend of the wait types ticked, live
/// or over a past range, above the top waits for that range, since the server started while live.
/// Live, the waits are read when the page opens and on Refresh, and while Resume (▶) in the top bar
/// trends them the timer reads only the per-type totals. The verdict is the trend's while it draws
/// lines, else the top waits' (<see cref="WaitsVerdict"/>).
/// </summary>
public sealed partial class WaitsViewModel : PageViewModel
{
    /// <summary>How many readings a live line keeps.</summary>
    internal const int MaxPoints = 60;

    /// <summary>How many wait types the chart draws at once.</summary>
    internal const int MaxSeries = 12;

    private const string NoLiveDataText = "Select wait types on the left, then press Resume (▶) in the top bar to begin trending";
    private const string NotRecordedText =
        "Waits per type are recorded every few minutes while SqlVitals monitors this connection (Settings → Monitoring & history).";

    private enum Metric { WaitTime, SignalWait, ResourceWait, WaitingTasks }

    private readonly IWaitsRepository _repo;
    private readonly ConnectionHistory? _history;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;

    // Every wait type known, in the server's order; the list shows those the search matches.
    private readonly List<WaitTypeItem> _allItems = [];

    // The live lines, by wait type, and the totals read last for the next reading's rate.
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _windows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (long WaitTimeMs, long SignalWaitTimeMs, long WaitingTasksCount)> _prevValues = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _prevTime;
    private bool _initialized;

    // Past ranges come from the history's detail snapshots (waits per type, every few minutes). The
    // live lines are kept while one is shown, so switching back to Live loses nothing.
    private HistoryRange _range = HistoryRange.Live;
    private int _historyVersion;   // bumped per load; a stale load is dropped
    private HistoryLoad<WaitHistoryBucket>? _historyLoad;
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _historyWindows = new(StringComparer.OrdinalIgnoreCase);

    // "Compare to baseline" draws the same time last week under each wait type, dashed. It is
    // clipped to the chart on every redraw, so it follows the live window as it moves.
    private readonly BaselineTracker<WaitHistoryBucket>? _baseline;
    private string _baselineNote = "";
    private string _statusMain   = "Ready — tick wait types, then press Resume (▶) in the top bar";

    // The top waits: the server's since it started, or the range's from the history.
    private IReadOnlyList<TopWaitType>? _liveTop;
    private IReadOnlyList<TopWaitType>? _historyTop;
    private IReadOnlyList<TopWaitType>? TopShown => _range.IsLive ? _liveTop : _historyTop;
    private string? During => _range.IsLive ? null : $"over the {_range.Label.ToLowerInvariant()}";

    // The category the top waits' verdict names, for its cause.
    private string _category = string.Empty;

    // Ticks set many at once (Top 20, Clear All) draw once, after.
    private bool _ticking;

    /// <param name="history">The connection's monitoring history; null for a connection that isn't saved.</param>
    /// <param name="navigator">Where the top waits' cause goes: Processes, Resource Queries, File I/O, Memory.</param>
    public WaitsViewModel(IWaitsRepository repo, ConnectionHistory? history, INavigator navigator, TimeProvider? time = null)
    {
        _repo      = repo;
        _history   = history;
        _navigator = navigator;
        _time      = time ?? TimeProvider.System;
        if (history is not null)
            _baseline = new BaselineTracker<WaitHistoryBucket>(history, (reader, id, from, to, bucket) => reader.ReadWaits(id, from, to, bucket));

        // Not reading until Resume (▶) in the top bar.
        SetPaused(true);
        ShowsCountdown  = true;
        RefreshInterval = TimeSpan.FromSeconds(IntervalSeconds);
        StatusLine      = _statusMain;
        PropertyChanged += OnOwnPropertyChanged;
        UpdateVerdict();
    }

    // Paused or resumed from the top bar: the verdict says whether it trends.
    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsPaused))
            return;
        ReportContextChanged();
        UpdateVerdict();
    }

    // ── What the top bar shows ────────────────────────────────────────────

    /// <summary>The monitoring history can be read: the range picker offers past ranges.</summary>
    public bool HistoryAvailable => _history is not null;

    /// <summary>Live, or a past range from the monitoring history.</summary>
    public bool IsLive => _range.IsLive;

    /// <summary>The newest data a past range shows, on this PC's clock; null live.</summary>
    public DateTime? DataTo => _range.IsLive ? null : PageBar.DataTo(_historyLoad, b => b.TimeUtc);

    // A past range doesn't trend: Resume waits for Live.
    protected override bool CanResume => _range.IsLive;

    // ── What is read, and how often ───────────────────────────────────────

    /// <summary>How often the waits are read while live and trending, in seconds.</summary>
    public IReadOnlyList<int> IntervalChoices { get; } = [5, 10, 30, 60];

    public IReadOnlyList<string> IntervalNames { get; } = ["5s", "10s", "30s", "60s"];

    [ObservableProperty]
    private int _intervalIndex = 1;

    public int IntervalSeconds => IntervalChoices[Math.Clamp(IntervalIndex, 0, IntervalChoices.Count - 1)];

    // Another interval: the wait for the next read starts again.
    partial void OnIntervalIndexChanged(int value)
    {
        RefreshInterval = TimeSpan.FromSeconds(IntervalSeconds);
        ReportContextChanged();
    }

    /// <summary>What the lines show, per second.</summary>
    public IReadOnlyList<string> MetricNames { get; } = ["Wait Time (ms/sec)", "Signal Wait (ms/sec)", "Resource Wait (ms/sec)", "Waiting Tasks/sec"];

    [ObservableProperty]
    private int _metricIndex;

    private Metric ChosenMetric => (Metric)Math.Clamp(MetricIndex, 0, MetricNames.Count - 1);

    // Another metric: the live lines start again with fresh rates; a past range's are worked out
    // again from what was read.
    partial void OnMetricIndexChanged(int value)
    {
        _windows.Clear();
        _historyWindows.Clear();
        Redraw();
    }

    /// <summary>
    /// A range was picked in the top bar: a past one is read from the history, and the live reads
    /// stop meanwhile; Live draws the live lines kept, and Resume (▶) trends again.
    /// </summary>
    public void PickRange(HistoryRange range)
    {
        _range = range;
        _historyVersion++;   // a read of the range before is dropped when it ends
        if (!range.IsLive)
            SetPaused(true);
        OnPropertyChanged(nameof(IsLive));
        ReportContextChanged();

        // A baseline belongs to the range it was read for.
        _baseline?.Clear();
        _baselineNote = "";

        if (range.IsLive)
        {
            _historyLoad = null;
            _historyTop  = null;
            _historyWindows.Clear();
            ShowTopWaits();
            ShowStatus("Live — press Resume (▶) in the top bar to trend");
            Redraw();
            _ = LoadBaselineAsync();
            return;
        }

        // The live table isn't the range's: empty until the range is read.
        _historyTop = null;
        ShowTopWaits();
        _ = RereadAsync();
    }

    /// <summary>Compare to baseline in the top bar: last week's line under each.</summary>
    [ObservableProperty]
    private bool _compareToBaseline;

    partial void OnCompareToBaselineChanged(bool value)
    {
        _baseline?.Clear();
        _baselineNote = "";
        Redraw();
        ShowStatus(_statusMain);
        _ = LoadBaselineAsync();
    }

    // ── The wait types ────────────────────────────────────────────────────

    /// <summary>Filters the list by name.</summary>
    [ObservableProperty]
    private string _search = "";

    partial void OnSearchChanged(string value) => ShowList();

    /// <summary>The wait types the search matches, to tick.</summary>
    [ObservableProperty]
    private IReadOnlyList<WaitTypeItem> _waitTypes = [];

    /// <summary>"12 / 180 selected".</summary>
    [ObservableProperty]
    private string _selectionCount = "0 / 0 selected";

    /// <summary>The wait types read, in the server's order; for tests.</summary>
    internal IReadOnlyList<WaitTypeItem> AllWaitTypes => _allItems;

    // Top 20, the button or the verdict's action: the 20 that waited most, charted.
    [RelayCommand]
    private void ChartTopWaits()
    {
        if (_range.IsLive ? !_initialized : _historyLoad is null)
            return;
        Tick(CheckTopWaits);
    }

    [RelayCommand]
    private void ClearAll() => Tick(() =>
    {
        foreach (var item in _allItems)
            item.IsChecked = false;
    });

    // Ticks many at once, then draws once.
    private void Tick(Action change)
    {
        _ticking = true;
        try
        {
            change();
        }
        finally
        {
            _ticking = false;
        }
        ShowSelectionCount();
        Redraw();
    }

    private WaitTypeItem Add(string waitType, string category)
    {
        var item = new WaitTypeItem(waitType, category);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(WaitTypeItem.IsChecked) || _ticking)
                return;
            ShowSelectionCount();
            Redraw();
        };
        _allItems.Add(item);
        return item;
    }

    // Top 20 by total wait time: over the range on screen, or since the server started when live.
    private void CheckTopWaits()
    {
        IEnumerable<string> ranked = !_range.IsLive && _historyTop is { } top
            ? top.Select(w => w.WaitType)
            : _prevValues.OrderByDescending(kv => kv.Value.WaitTimeMs).Select(kv => kv.Key);

        var top20 = ranked.Take(20).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allItems)
            item.IsChecked = top20.Contains(item.WaitType);
    }

    private void ShowList() =>
        WaitTypes = string.IsNullOrWhiteSpace(Search)
            ? _allItems.ToList()
            : _allItems.Where(i => i.WaitType.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToList();

    private void ShowSelectionCount() => SelectionCount = $"{_allItems.Count(i => i.IsChecked)} / {_allItems.Count} selected";

    // ── The chart ─────────────────────────────────────────────────────────

    /// <summary>What the trend draws.</summary>
    [ObservableProperty]
    private TrendChart _chart = TrendChart.Empty;

    /// <summary>A reading was added to the lines <see cref="Chart"/> draws: the chart draws it now, without being built again.</summary>
    public event Action? Sampled;

    /// <summary>Why the trend is empty, or null while it draws lines.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoData))]
    private string? _noData = NoLiveDataText;

    public bool ShowsNoData => NoData is not null;

    /// <summary>"⚠  Max 12 series — first 12 shown" while more are ticked; empty otherwise.</summary>
    [ObservableProperty]
    private string _maxNote = "";

    /// <summary>What was read last and when, and the baseline's note while it is on.</summary>
    [ObservableProperty]
    private string _statusLine = "";

    // Sets the status line, adding what the baseline shows while it is on.
    private void ShowStatus(string text)
    {
        _statusMain = text;
        StatusLine  = CompareToBaseline && _baselineNote.Length > 0 ? $"{text}  |  {_baselineNote}" : text;
    }

    // The lines for the wait types ticked, and the verdict on them.
    private void Redraw()
    {
        var toShow = _allItems.Where(i => i.IsChecked).Take(MaxSeries).ToList();
        MaxNote = _allItems.Count(i => i.IsChecked) > MaxSeries ? $"⚠  Max {MaxSeries} series — first {MaxSeries} shown" : "";

        var hasData = _range.IsLive ? _windows.Count > 0 : _historyLoad is { Items.Count: > 0 };
        if (toShow.Count == 0 || !hasData)
        {
            NoData = _range.IsLive || _historyLoad is null ? NoLiveDataText
                   : toShow.Count == 0 ? "Select wait types on the left to chart them."
                   : _historyLoad.NoData(NotRecordedText);
            Chart = TrendChart.Empty;
            UpdateVerdict();
            return;
        }
        NoData = null;

        var windows = _range.IsLive ? _windows : _historyWindows;
        var lines = toShow.Select((item, idx) =>
        {
            if (!windows.TryGetValue(item.WaitType, out var points))
            {
                points = _range.IsLive ? [] : HistoryWindow(item.WaitType);
                windows[item.WaitType] = points;
            }
            return new TrendLine(item.WaitType, points, idx);
        }).ToList();

        // A past range is shown whole, so missing history reads as missing rather than cropped.
        var history = _range.IsLive ? null : _historyLoad;

        // The baseline under each line, over the times the chart shows: the range picked, or the
        // live window so far (so the baseline never stretches the live axis).
        List<TrendLine> baselines = [];
        if (CompareToBaseline && _baseline?.Current is { } baseline)
        {
            var live = lines.Select(l => l.Points).Where(p => p.Count > 0).ToList();
            (DateTime From, DateTime To)? shown = history is not null
                ? (history.FromUtc.ToLocalTime(), history.ToUtc.ToLocalTime())
                : live.Count > 0 ? (live.Min(p => p[0].DateTime), live.Max(p => p[^1].DateTime)) : null;
            if (shown is { } s)
                baselines.AddRange(lines.Select(l => l with { Points = BaselineWindow(baseline, l.Name, s.From, s.To) }));
        }

        Chart = new TrendChart(lines, baselines, history?.FromUtc.ToLocalTime(), history?.ToUtc.ToLocalTime(),
                               history is null ? "HH:mm:ss" : ConnectionHistory.AxisFormat(history.Span),
                               ChosenMetric == Metric.WaitingTasks ? "tasks / sec" : "ms / sec");
        UpdateVerdict();
    }

    // ── The top waits ─────────────────────────────────────────────────────

    /// <summary>The top waits for the range on screen; empty until read.</summary>
    public GridRows<TopWaitType> TopWaits { get; } = [];

    [ObservableProperty]
    private string _topHeader = "Top wait types since the server started";

    /// <summary>Folded, the top 15 say it in one line (#149): "PAGEIOLATCH_SH 41.0 % · WRITELOG 12.3 %".</summary>
    [ObservableProperty]
    private string _topSummary = "Not read yet";

    private void ShowTopWaits()
    {
        var data = TopShown ?? [];
        TopWaits.Replace(data);
        TopHeader = _range.IsLive
            ? "Top wait types since the server started"
            : $"Top wait types {During}, from the monitoring history";
        TopSummary = TopShown is null ? "Not read yet"
            : data.Count == 0 ? "No waits worth noting"
            : string.Join(" · ", data.Take(3).Select(w => string.Format(CultureInfo.CurrentCulture, "{0} {1:N1} %", w.WaitType, w.PctOfTotal)));
        UpdateVerdict();
    }

    // ── Reading ───────────────────────────────────────────────────────────

    protected override Task LoadAsync(CancellationToken cancel) => LoadAsync(PageRead.Refresh, cancel);

    // A past range is read again, so "last hour" moves up to now. Live, the top waits are read with
    // the waits: when the page opens and on Refresh, not on every tick of the trend.
    protected override async Task LoadAsync(PageRead read, CancellationToken cancel)
    {
        if (!_range.IsLive)
        {
            await LoadHistoryAsync(cancel);
            return;
        }
        if (read == PageRead.Timed)
        {
            await LoadSnapshotAsync(cancel);
            return;
        }

        // Both at once; the trend reads even when the table can't, as Wait Trend did alone.
        var top = LoadTopWaitsAsync(cancel);
        try
        {
            await LoadSnapshotAsync(cancel);
        }
        catch
        {
            // The table's read ends before the failure is reported, so nothing lands after it.
            try { await top; } catch (OperationCanceledException) { }
            throw;
        }
        if (await top is { } failed)
            ExceptionDispatchInfo.Throw(failed);
    }

    protected override void ShowLoadError(Exception exception) =>
        ShowError(_range.IsLive ? "the wait statistics" : "the monitoring history", exception);

    // What went wrong, for LoadAsync to report once the trend has read; null when it was read.
    private async Task<Exception?> LoadTopWaitsAsync(CancellationToken cancel)
    {
        try
        {
            _liveTop = (await _repo.GetTopWaitTypesAsync(cancel)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // What was read before stays; nothing read yet says so.
            ShowTopWaits();
            return ex;
        }
        ShowTopWaits();
        return null;
    }

    private async Task LoadSnapshotAsync(CancellationToken cancel)
    {
        List<WaitStatCumulative> data;
        try
        {
            StatusLine = "Querying sys.dm_os_wait_stats…";
            data = (await _repo.GetCumulativeWaitsAsync(cancel)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A past range picked meanwhile owns the status line and the verdict.
            if (!_range.IsLive)
                return;
            ShowStatus($"Error: {ex.Message}");
            throw;
        }
        var now = _time.GetLocalNow().DateTime;

        if (!_initialized)
        {
            ListWaitTypes(data);
            StoreValues(data, now);
            _initialized = true;
            ShowStatus($"Loaded {_allItems.Count} wait types. Select types, then press Resume (▶) in the top bar.");
            UpdateVerdict();
            return;   // the first reading: no rate yet
        }

        var elapsedSec = Math.Max(0.1, (now - _prevTime).TotalSeconds);
        foreach (var w in data)
        {
            if (!_prevValues.TryGetValue(w.WaitType, out var prev))
                continue;
            if (!_windows.TryGetValue(w.WaitType, out var window))
                _windows[w.WaitType] = window = [];

            var value = ChosenMetric switch
            {
                Metric.SignalWait   => (w.SignalWaitTimeMs - prev.SignalWaitTimeMs) / elapsedSec,
                Metric.ResourceWait => ((w.WaitTimeMs - w.SignalWaitTimeMs) - (prev.WaitTimeMs - prev.SignalWaitTimeMs)) / elapsedSec,
                Metric.WaitingTasks => (w.WaitingTasksCount - prev.WaitingTasksCount) / elapsedSec,
                _                   => (w.WaitTimeMs - prev.WaitTimeMs) / elapsedSec,
            };

            // A restart of the server sets the totals back to zero.
            window.Add(new DateTimePoint(now, Math.Max(0d, value)));
            if (window.Count > MaxPoints)
                window.RemoveAt(0);
        }
        StoreValues(data, now);

        // A read that finishes after a past range was picked only fills the live lines.
        if (!_range.IsLive)
            return;

        // The lines drawn are the live ones, which have just moved; the baseline is clipped to
        // them, so it is drawn again.
        if (CompareToBaseline || !DrawsLiveLines())
            Redraw();
        else
        {
            Sampled?.Invoke();
            UpdateVerdict();
        }
        ShowStatus($"Updated {now:HH:mm:ss}  |  {_allItems.Count(i => i.IsChecked)} series shown");
        await LoadBaselineAsync();
    }

    // The chart draws the live lines of the wait types ticked, as they are now.
    private bool DrawsLiveLines()
    {
        var ticked = _allItems.Where(i => i.IsChecked).Take(MaxSeries).ToList();
        return Chart.Lines.Count == ticked.Count && ticked.Count > 0
            && Chart.Lines.Zip(ticked).All(p => p.First.Name == p.Second.WaitType
                                                && _windows.TryGetValue(p.Second.WaitType, out var points) && ReferenceEquals(points, p.First.Points));
    }

    // The wait types the server lists, the 20 that waited most ticked.
    private void ListWaitTypes(List<WaitStatCumulative> data)
    {
        _allItems.Clear();
        var top20 = data.OrderByDescending(w => w.WaitTimeMs).Take(20).Select(w => w.WaitType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ticking = true;
        foreach (var w in data)
            Add(w.WaitType, w.WaitCategory).IsChecked = top20.Contains(w.WaitType);
        _ticking = false;
        ShowList();
        ShowSelectionCount();
    }

    private void StoreValues(List<WaitStatCumulative> data, DateTime captureTime)
    {
        _prevValues = data.GroupBy(w => w.WaitType, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => (g.First().WaitTimeMs, g.First().SignalWaitTimeMs, g.First().WaitingTasksCount),
                                        StringComparer.OrdinalIgnoreCase);
        _prevTime = captureTime;
    }

    // ── A past range ──────────────────────────────────────────────────────

    private async Task LoadHistoryAsync(CancellationToken cancel)
    {
        if (_history is null)
            return;

        var version = ++_historyVersion;
        var range   = _range;
        StatusLine = $"Reading {range.Label.ToLowerInvariant()} from history…";
        HistoryLoad<WaitHistoryBucket> load;
        try
        {
            load = await _history.LoadAsync(range, (reader, id, from, to, bucket) => reader.ReadWaits(id, from, to, bucket));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version != _historyVersion)
                return;   // another range was picked meanwhile: this one's failure is no longer on screen
            ShowStatus($"Could not read the history: {ex.Message}");
            throw;
        }
        cancel.ThrowIfCancellationRequested();
        if (version != _historyVersion)
            return;   // the user picked something else meanwhile

        _historyLoad = load;
        _historyWindows.Clear();
        _historyTop  = HistoryTopWaits.Over(load.Items, load.ToUtc.ToLocalTime());
        ShowTopWaits();

        // Add waits the list lacks: it was opened on a past range before any live read, or the
        // range has waits the server no longer lists.
        var recorded = load.Items.SelectMany(b => b.Waits.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known    = _allItems.Select(i => i.WaitType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ticking = true;
        foreach (var type in recorded.Where(t => !known.Contains(t)).Order())
            Add(type, "");

        // The live list starts on the top waits since the server started, mostly idle ones the
        // history leaves out. Rather than a chart of flat zeros, start on the range's top waits.
        if (recorded.Count > 0 && !_allItems.Any(i => i.IsChecked && recorded.Contains(i.WaitType)))
            CheckTopWaits();
        _ticking = false;

        ShowList();
        ShowSelectionCount();
        Redraw();
        ShowStatus(load.Items.Count == 0 ? "No history in this range" : load.Describe(load.Items.Select(b => b.TimeUtc).ToList()));
        ReportContextChanged();
        await LoadBaselineAsync();
    }

    // The chosen metric per second over each bucket, with breaks where nothing was recorded.
    private ObservableCollection<DateTimePoint> HistoryWindow(string waitType)
    {
        var window = new ObservableCollection<DateTimePoint>();
        if (_historyLoad is not { } load)
            return window;
        foreach (var (time, bucket) in OnLocalClock(load.Items, load.Bucket))
            window.Add(new DateTimePoint(time, bucket is null ? null : ValueOf(bucket, waitType)));
        return window;
    }

    // History buckets on this PC's clock, as the chart plots them, with a null where the line breaks.
    private static IEnumerable<(DateTime Time, WaitHistoryBucket? Bucket)> OnLocalClock(IReadOnlyList<WaitHistoryBucket> items, TimeSpan bucket) =>
        HistoryGaps.WithGaps(items, b => b.TimeUtc, bucket).Select(p => (p.Time.ToLocalTime(), p.Item));

    // The chosen metric per second over one bucket. A bucket without this wait type had none of
    // it: zero, not a break.
    private double ValueOf(WaitHistoryBucket bucket, string waitType)
    {
        bucket.Waits.TryGetValue(waitType, out var w);
        long total = w is null ? 0 : ChosenMetric switch
        {
            Metric.SignalWait   => w.SignalWaitTimeMs,
            Metric.ResourceWait => w.WaitTimeMs - w.SignalWaitTimeMs,
            Metric.WaitingTasks => w.WaitingTasks,
            _                   => w.WaitTimeMs,
        };
        return bucket.Seconds > 0 ? Math.Max(0, total) / bucket.Seconds : 0;
    }

    // ── The baseline ──────────────────────────────────────────────────────

    // Reads the baseline for what the chart shows, when it doesn't have one yet: the past range on
    // screen, or the live window. Live, that is a new read only every half hour or so.
    [ReportsItsErrors]
    private async Task LoadBaselineAsync()
    {
        if (_baseline is null || !CompareToBaseline)
            return;
        try
        {
            var read = _range.IsLive
                ? await _baseline.EnsureLiveAsync(LiveSpan())
                : _historyLoad is { } load && await _baseline.LoadAsync(load.FromUtc, load.ToUtc, load.Bucket);
            if (!read || _baseline.Current is not { } baseline)
                return;

            _baselineNote = baseline.Describe();
            Redraw();
            ShowStatus(_statusMain);
        }
        catch (Exception ex)
        {
            _baselineNote = $"Could not read the baseline: {ex.Message}";
            ShowStatus(_statusMain);
            ReportSideError("Waits baseline", ex);
        }
    }

    // How long the live lines on the chart span.
    private TimeSpan LiveSpan()
    {
        var shown = _windows.Values.Where(w => w.Count > 0).ToList();
        return shown.Count == 0 ? TimeSpan.Zero : shown.Max(w => w[^1].DateTime) - shown.Min(w => w[0].DateTime);
    }

    // Last week's line for one wait type, moved onto this week's axis between from and to.
    private ObservableCollection<DateTimePoint> BaselineWindow(BaselineLoad<WaitHistoryBucket> baseline, string waitType, DateTime from, DateTime to)
    {
        var window = new ObservableCollection<DateTimePoint>();
        foreach (var (time, bucket) in HistoryBaseline.Shift(OnLocalClock(baseline.Items, baseline.Bucket), from, to))
            window.Add(new DateTimePoint(time, bucket is null ? null : ValueOf(bucket, waitType)));
        return window;
    }

    // ── The verdict ───────────────────────────────────────────────────────

    // The lines on the chart, as the chart has them: the wait types ticked (at most MaxSeries).
    private void UpdateVerdict()
    {
        var windows = _range.IsLive ? _windows : _historyWindows;
        var series  = _allItems.Where(i => i.IsChecked).Take(MaxSeries)
            .Select(i => new WaitTrendSeries(i.WaitType, windows.TryGetValue(i.WaitType, out var points) ? points.Select(p => p.Value).ToList() : []))
            .ToList();
        var state  = !_range.IsLive ? WaitTrendState.History : !IsPaused ? WaitTrendState.Live : WaitTrendState.NotStarted;
        var unit   = ChosenMetric == Metric.WaitingTasks ? "tasks/s" : "ms/s";
        var noData = _range.IsLive || _historyLoad is null ? null : _historyLoad.NoData(NotRecordedText);
        var top    = TopShown;

        Verdict   = WaitsVerdict.From(top, During, series, state, unit, Over(windows), noData);
        _category = top is not { Count: > 0 } ? string.Empty
            : top.GroupBy(w => w.WaitCategory).OrderByDescending(g => g.Sum(w => w.WaitTimeMs)).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    // "over the last 4 min", "over the last 24 hours".
    private string Over(Dictionary<string, ObservableCollection<DateTimePoint>> windows)
    {
        if (!_range.IsLive)
            return $"over the {_range.Label.ToLowerInvariant()}";
        var shown = windows.Values.Where(w => w.Count > 1).ToList();
        if (shown.Count == 0)
            return "since reading started";
        var span = shown.Max(w => w[^1].DateTime) - shown.Min(w => w[0].DateTime);
        return span.TotalMinutes >= 2 ? $"over the last {span.TotalMinutes:0} min" : $"over the last {Math.Max(1, span.TotalSeconds):0} s";
    }

    protected override Task OnActionAsync(VerdictAction action)
    {
        switch (action.Key)
        {
            case TopWaitsVerdict.Cause when TopWaitsVerdict.CausePage(_category) is { } page:
                return _navigator.GoToAsync(page.Tag);
            case WaitTrendVerdict.TopWaits:
                ChartTopWaits();
                break;
            case WaitsVerdict.StartTrend:
                SetPaused(false);
                break;
        }
        return Task.CompletedTask;
    }
}
