using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// Perfmon (#150, #188): the counters ticked, charted live or over a past range, a group of them
/// ticked to start with. Read when the page opens, on Refresh, and while Resume (▶) in the top bar
/// runs it. The verdict names the counters past a known limit, and the ones rising.
/// </summary>
public sealed partial class PerfmonViewModel : PageViewModel
{
    /// <summary>How many readings a live line keeps.</summary>
    internal const int MaxPoints = 60;

    /// <summary>How many counters the chart draws at once.</summary>
    internal const int MaxSeries = 12;

    // The counter groups, as SQL Server's own performance packs group them.
    internal static readonly IReadOnlyList<string> GroupNames =
    [
        "General Throughput",
        "Memory Pressure",
        "CPU / Compilation",
        "I/O Pressure",
        "TempDB Pressure",
        "Lock / Blocking",
    ];

    private static readonly Dictionary<string, string[]> Groups = new()
    {
        ["General Throughput"] =
        [
            "Batch Requests/sec",
            "SQL Compilations/sec",
            "SQL Re-Compilations/sec",
            "Query optimizations/sec",
            "Network IO waits",
        ],
        ["Memory Pressure"] =
        [
            "Memory Grants Pending",
            "Granted Workspace Memory (KB)",
            "Target Server Memory (KB)",
            "Total Server Memory (KB)",
            "Stolen Server Memory (KB)",
            "Lock Memory (KB)",
            "SQL Cache Memory (KB)",
            "Lazy writes/sec",
            "Free list stalls/sec",
            "Reduced memory grants/sec",
            "Memory grant queue waits",
            "Thread-safe memory objects waits",
            "Page reads/sec",
            "Readahead pages/sec",
        ],
        ["CPU / Compilation"] =
        [
            "Batch Requests/sec",
            "SQL Compilations/sec",
            "SQL Re-Compilations/sec",
            "Query optimizations/sec",
            "Active parallel threads",
            "Active requests",
            "Queued requests",
            "Wait for the worker",
        ],
        ["I/O Pressure"] =
        [
            "Page reads/sec",
            "Page writes/sec",
            "Checkpoint pages/sec",
            "Page lookups/sec",
            "Readahead pages/sec",
            "Background writer pages/sec",
            "Log Flushes/sec",
            "Log Bytes Flushed/sec",
            "Log Flush Write Time (ms)",
            "Page IO latch waits",
            "Log buffer waits",
            "Log write waits",
            "Full Scans/sec",
            "Index Searches/sec",
            "Page Splits/sec",
            "Forwarded Records/sec",
        ],
        ["TempDB Pressure"] =
        [
            "Version Store Size (KB)",
            "Free Space in tempdb (KB)",
            "Active Temp Tables",
            "Version Generation rate (KB/s)",
            "Version Cleanup rate (KB/s)",
            "Temp Tables Creation Rate",
            "Workfiles Created/sec",
            "Worktables Created/sec",
        ],
        ["Lock / Blocking"] =
        [
            "Lock Requests/sec",
            "Lock Wait Time (ms)",
            "Lock Waits/sec",
            "Number of Deadlocks/sec",
            "Table Lock Escalations/sec",
            "Blocked tasks",
            "Lock waits",
            "Non-Page latch waits",
            "Page latch waits",
            "Processes blocked",
            "Lock Timeouts/sec",
        ],
    };

    // cntr_type 272696576 is a cumulative rate counter, worked out from the difference between two
    // readings; any other is a value as it is.
    private const int RateCntrType = LiveMetricsRepository.PerfmonRateCounterType;

    private const string NoLiveDataText = "Select counters from the left panel to begin charting.";
    private const string NotRecordedText =
        "Perfmon counters are recorded with each history snapshot (every few minutes) while SqlVitals " +
        "monitors this connection. Versions before 0.30 didn't record them.";

    private readonly ILiveMetricsRepository _repo;
    private readonly ConnectionHistory? _history;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;

    // Every counter known, by name; the list shows those the search matches.
    private List<CounterItem> _allItems = [];

    // The live lines, by counter, and each counter's value read last for the next rate.
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _windows = [];
    private readonly Dictionary<string, long> _prevValues = [];
    private DateTime _prevTime = DateTime.MinValue;

    // Past ranges come from the counters recorded with each history detail snapshot. The live
    // lines are kept while one is shown, so switching back to Live loses nothing.
    private HistoryRange _range = HistoryRange.Live;
    private int _historyVersion;   // bumped per load; a stale load is dropped
    private HistoryLoad<CounterHistoryBucket>? _historyLoad;
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _historyWindows = [];

    // "Compare to baseline" draws the same time last week under each counter, dashed. It is
    // clipped to the chart on every redraw, so it follows the live window as it moves.
    private readonly BaselineTracker<CounterHistoryBucket>? _baseline;
    private string _baselineNote = "";
    private string _statusMain   = "Not reading: Resume (▶) in the top bar starts it, Refresh reads once.";

    // The page the verdict's cause opens.
    private string? _causePage;

    // Ticks set many at once (a group, Select All) draw once, after.
    private bool _ticking;

    /// <param name="history">The connection's monitoring history; null for a connection that isn't saved.</param>
    /// <param name="navigator">Where the verdict's cause goes.</param>
    public PerfmonViewModel(ILiveMetricsRepository repo, ConnectionHistory? history, INavigator navigator, TimeProvider? time = null)
    {
        _repo      = repo;
        _history   = history;
        _navigator = navigator;
        _time      = time ?? TimeProvider.System;
        if (history is not null)
            _baseline = new BaselineTracker<CounterHistoryBucket>(history, (reader, id, from, to, bucket) => reader.ReadCounters(id, from, to, bucket));

        // Not reading until Resume (▶) in the top bar.
        SetPaused(true);
        ShowsCountdown  = true;
        RefreshInterval = TimeSpan.FromSeconds(IntervalSeconds);
        StatusLine      = _statusMain;
        PropertyChanged += OnOwnPropertyChanged;
        Redraw();   // nothing ticked yet: the chart says how to start
    }

    // Paused or resumed from the top bar: the verdict says whether it is charting.
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

    // A past range isn't read live: Resume waits for Live.
    protected override bool CanResume => _range.IsLive;

    // ── How often ─────────────────────────────────────────────────────────

    /// <summary>How often the counters are read while live and running, in seconds: every 5 s up to 2 min.</summary>
    public IReadOnlyList<int> IntervalChoices { get; } = Enumerable.Range(1, 24).Select(i => i * 5).ToArray();

    public IReadOnlyList<string> IntervalNames => IntervalChoices.Select(s => s < 60 ? $"{s}s" : $"{s / 60}m {s % 60:00}s").ToList();

    [ObservableProperty]
    private int _intervalIndex = 1;   // 10 s

    public int IntervalSeconds => IntervalChoices[Math.Clamp(IntervalIndex, 0, IntervalChoices.Count - 1)];

    // Another interval: the wait for the next read starts again.
    partial void OnIntervalIndexChanged(int value)
    {
        RefreshInterval = TimeSpan.FromSeconds(IntervalSeconds);
        ReportContextChanged();
    }

    /// <summary>
    /// A range was picked in the top bar: a past one is read from the history, and the live reads
    /// stop meanwhile; Live draws the live lines kept, and Resume (▶) reads again.
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
            _historyWindows.Clear();
            ShowStatus("Live: Resume (▶) in the top bar starts reading, Refresh reads once.");
            Redraw();
            _ = LoadBaselineAsync();
            return;
        }
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

    // ── The counters ──────────────────────────────────────────────────────

    /// <summary>The groups offered.</summary>
    public IReadOnlyList<string> GroupChoices { get; } = GroupNames;

    /// <summary>The group whose counters are ticked: picked, its counters replace the ticks.</summary>
    [ObservableProperty]
    private string? _group = GroupNames[0];

    partial void OnGroupChanged(string? value)
    {
        if (value is not null)
            ApplyGroup(value);
    }

    /// <summary>Filters the list by name.</summary>
    [ObservableProperty]
    private string _search = "";

    partial void OnSearchChanged(string value) => ShowList();

    /// <summary>The counters the search matches, to tick.</summary>
    [ObservableProperty]
    private IReadOnlyList<CounterItem> _counters = [];

    /// <summary>The counters read; for tests.</summary>
    internal IReadOnlyList<CounterItem> AllCounters => _allItems;

    // The first MaxSeries counters, ticked.
    [RelayCommand]
    private void SelectAll() => Tick(() =>
    {
        var applied = 0;
        foreach (var item in _allItems)
        {
            item.IsChecked = applied < MaxSeries;
            if (item.IsChecked)
                applied++;
        }
    });

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
        ShowList();
        Redraw();
    }

    // One counter ticked or not by the user: past MaxSeries, the tick is taken back.
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CounterItem.IsChecked) || _ticking || sender is not CounterItem item)
            return;
        if (item.IsChecked && _allItems.Count(i => i.IsChecked) > MaxSeries)
        {
            _ticking = true;
            item.IsChecked = false;
            _ticking = false;
            StatusLine = $"Maximum {MaxSeries} counters — deselect one first.";
            return;
        }
        Redraw();
    }

    // The group's counters ticked, at most MaxSeries, and the search cleared to show them.
    private void ApplyGroup(string groupName)
    {
        if (!Groups.TryGetValue(groupName, out var counters))
            return;
        _ticking = true;
        foreach (var item in _allItems)
            item.IsChecked = false;
        var applied = 0;
        foreach (var name in counters)
        {
            if (applied < MaxSeries && _allItems.FirstOrDefault(i => string.Equals(i.CounterName, name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                match.IsChecked = true;
                applied++;
            }
        }
        _ticking = false;
        Search = "";
        ShowList();
        Redraw();
    }

    private void ShowList() =>
        Counters = string.IsNullOrEmpty(Search.Trim())
            ? _allItems.ToList()
            : _allItems.Where(c => c.DisplayName.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    private void ListCounters(IEnumerable<CounterItem> items)
    {
        // Only the counters some group has.
        var known = Groups.Values.SelectMany(g => g).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allItems = items.Where(c => known.Contains(c.CounterName)).OrderBy(c => c.CounterName).ToList();
        foreach (var item in _allItems)
        {
            item.PropertyChanged += OnItemChanged;
            if (!_windows.ContainsKey(item.CounterName))
                _windows[item.CounterName] = [];
        }

        // The group picked ticked: the first, unless another was picked before anything was read.
        ApplyGroup(Group ?? GroupNames[0]);
    }

    // "SQLServer:SQL Statistics" → "SQL Statistics".
    private static string ShortObjectName(string full)
    {
        var idx = full.IndexOf(':');
        return idx >= 0 ? full[(idx + 1)..].Trim() : full.Trim();
    }

    // ── The chart ─────────────────────────────────────────────────────────

    /// <summary>What the chart draws.</summary>
    [ObservableProperty]
    private TrendChart _chart = TrendChart.Empty;

    /// <summary>A reading was added to the live lines: the chart draws it now.</summary>
    public event Action? Sampled;

    /// <summary>Why the chart is empty, or null while it draws lines.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoData))]
    private string? _noData;

    public bool ShowsNoData => NoData is not null;

    /// <summary>What was read last and when, and the baseline's note while it is on.</summary>
    [ObservableProperty]
    private string _statusLine = "";

    // Sets the status line, adding what the baseline shows while it is on.
    private void ShowStatus(string text)
    {
        _statusMain = text;
        StatusLine  = CompareToBaseline && _baselineNote.Length > 0 ? $"{text}  •  {_baselineNote}" : text;
    }

    // The lines for the counters ticked, and the verdict on them.
    private void Redraw()
    {
        var selected = _allItems.Where(i => i.IsChecked).Take(MaxSeries).ToList();
        var windows  = _range.IsLive ? _windows : _historyWindows;
        foreach (var item in selected)
            if (!windows.ContainsKey(item.CounterName))
                windows[item.CounterName] = _range.IsLive ? [] : HistoryWindow(item.CounterName);

        var history = _range.IsLive ? null : _historyLoad;
        if (selected.Count == 0 || history is { Items.Count: 0 })
        {
            NoData = selected.Count == 0 ? NoLiveDataText : history!.NoData(NotRecordedText);
            Chart  = TrendChart.Empty;
            UpdateVerdict();
            return;
        }
        NoData = null;

        var lines = selected.Select((item, idx) => new TrendLine(item.CounterName, windows[item.CounterName], idx)).ToList();

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

        // A past range is shown whole, so missing history reads as missing rather than cropped.
        Chart = new TrendChart(lines, baselines, history?.FromUtc.ToLocalTime(), history?.ToUtc.ToLocalTime(),
                               history is null ? "HH:mm:ss" : ConnectionHistory.AxisFormat(history.Span), "Value");
        UpdateVerdict();
    }

    // ── Reading ───────────────────────────────────────────────────────────

    // A past range is read again, so "last hour" moves up to now.
    protected override Task LoadAsync(CancellationToken cancel) =>
        _range.IsLive ? LoadLiveAsync(cancel) : LoadHistoryAsync(cancel);

    // Which read failed: live, the verdict says why only while nothing has been read, and the
    // lines so far stay drawn after; a past range's failure is said on the status line.
    protected override void ShowLoadError(Exception exception)
    {
        if (!_range.IsLive)
            ShowStatus($"Could not read the history: {exception.Message}");
        else if (_allItems.Count == 0)
            ShowError("the performance counters", exception);
    }

    private async Task LoadLiveAsync(CancellationToken cancel)
    {
        var data = (await _repo.GetPerfmonCountersAsync(cancel)).ToList();

        // The first reading lists the counters the server has.
        if (_allItems.Count == 0)
            ListCounters(data.GroupBy(c => c.CounterName).Select(g => new CounterItem { CounterName = g.Key, ObjectShort = ShortObjectName(g.First().ObjectName) }));

        var now  = _time.GetLocalNow().DateTime;
        var secs = _prevTime == DateTime.MinValue ? 1.0 : Math.Max(0.1, (now - _prevTime).TotalSeconds);
        _prevTime = now;

        var read = data.GroupBy(c => c.CounterName).ToDictionary(g => g.Key, g => g.First());
        foreach (var (name, window) in _windows)
        {
            if (!read.TryGetValue(name, out var counter))
                continue;
            var raw = counter.CntrValue;
            var value = counter.CntrType != RateCntrType ? raw
                      : _prevValues.TryGetValue(name, out var prev) ? Math.Max(0, (raw - prev) / secs)
                      : 0;
            _prevValues[name] = raw;
            window.Add(new DateTimePoint(now, value));
            while (window.Count > MaxPoints)
                window.RemoveAt(0);
        }

        // A read that finishes after a past range was picked only fills the live lines.
        if (!_range.IsLive)
            return;

        // The baseline is clipped to the live window, which has just moved.
        if (CompareToBaseline)
            Redraw();
        Sampled?.Invoke();
        UpdateVerdict();

        ShowStatus($"Last refresh: {now:HH:mm:ss}  •  {data.Count} counters retrieved");
        await LoadBaselineAsync();
    }

    // ── A past range ──────────────────────────────────────────────────────

    private async Task LoadHistoryAsync(CancellationToken cancel)
    {
        if (_history is null)
            return;

        var version = ++_historyVersion;
        StatusLine = $"Reading {_range.Label.ToLowerInvariant()} from history…";
        HistoryLoad<CounterHistoryBucket> load;
        try
        {
            load = await _history.LoadAsync(_range, (reader, id, from, to, bucket) => reader.ReadCounters(id, from, to, bucket));
        }
        catch (Exception) when (version != _historyVersion)
        {
            return;   // only the latest pick owns the status line
        }
        cancel.ThrowIfCancellationRequested();
        if (version != _historyVersion)
            return;   // the user picked something else meanwhile

        _historyLoad = load;
        _historyWindows.Clear();

        // Opened on a past range before any live read: offer the counters that were recorded.
        if (_allItems.Count == 0)
            ListCounters(load.Items.SelectMany(b => b.Values.Keys).Distinct().Select(n => new CounterItem { CounterName = n }));

        Redraw();
        ShowStatus(load.Items.Count == 0 ? "No history in this range" : load.Describe(load.Items.Select(b => b.TimeUtc).ToList()));
        ReportContextChanged();
        await LoadBaselineAsync();
    }

    // The counter averaged over each bucket, with breaks where nothing was recorded. A bucket
    // without this counter had it at zero: zero, not a break.
    private ObservableCollection<DateTimePoint> HistoryWindow(string counterName)
    {
        var window = new ObservableCollection<DateTimePoint>();
        if (_historyLoad is not { } load)
            return window;
        foreach (var (time, bucket) in OnLocalClock(load.Items, load.Bucket))
            window.Add(new DateTimePoint(time, bucket?.Values.GetValueOrDefault(counterName)));
        return window;
    }

    // History buckets on this PC's clock, as the chart plots them, with a null where the line breaks.
    private static IEnumerable<(DateTime Time, CounterHistoryBucket? Bucket)> OnLocalClock(IReadOnlyList<CounterHistoryBucket> items, TimeSpan bucket) =>
        HistoryGaps.WithGaps(items, b => b.TimeUtc, bucket).Select(p => (p.Time.ToLocalTime(), p.Item));

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
            if (!read || _baseline.Current is null)
                return;

            _baselineNote = _baseline.Current.Describe();
            Redraw();
            ShowStatus(_statusMain);
        }
        catch (Exception ex)
        {
            _baselineNote = $"Could not read the baseline: {ex.Message}";
            ShowStatus(_statusMain);
            ReportSideError("Perfmon baseline", ex);
        }
    }

    // How long the live lines on the chart span.
    private TimeSpan LiveSpan()
    {
        var shown = _windows.Values.Where(w => w.Count > 0).ToList();
        return shown.Count == 0 ? TimeSpan.Zero : shown.Max(w => w[^1].DateTime) - shown.Min(w => w[0].DateTime);
    }

    // Last week's line for one counter, moved onto this week's axis between from and to.
    private static ObservableCollection<DateTimePoint> BaselineWindow(BaselineLoad<CounterHistoryBucket> baseline, string counterName, DateTime from, DateTime to)
    {
        var window = new ObservableCollection<DateTimePoint>();
        foreach (var (time, bucket) in HistoryBaseline.Shift(OnLocalClock(baseline.Items, baseline.Bucket), from, to))
            window.Add(new DateTimePoint(time, bucket?.Values.GetValueOrDefault(counterName)));
        return window;
    }

    // ── The verdict ───────────────────────────────────────────────────────

    // The counters ticked, as the chart draws them, judged against their well-known limits. Before
    // the first read there are none to tick yet: the group picked is what will be charted.
    private void UpdateVerdict()
    {
        var windows = _range.IsLive ? _windows : _historyWindows;
        var names   = _allItems.Count == 0
            ? Groups[Group ?? GroupNames[0]].Take(MaxSeries)
            : _allItems.Where(i => i.IsChecked).Take(MaxSeries).Select(i => i.CounterName);
        var series = names
            .Select(name => new PerfmonSeries(name, windows.TryGetValue(name, out var w) ? w.Select(p => p.Value).ToList() : []))
            .ToList();
        var state  = !_range.IsLive ? WaitTrendState.History : !IsPaused ? WaitTrendState.Live : WaitTrendState.NotStarted;
        var noData = !_range.IsLive && _historyLoad is { Items.Count: 0 } load ? load.NoData(NotRecordedText) : null;

        Verdict    = PerfmonVerdict.From(series, state, noData);
        _causePage = PerfmonVerdict.Breached(series)?.Page;
    }

    protected override Task OnActionAsync(VerdictAction action) =>
        action.Key == PerfmonVerdict.Cause && _causePage is { } page ? _navigator.GoToAsync(page) : Task.CompletedTask;
}
