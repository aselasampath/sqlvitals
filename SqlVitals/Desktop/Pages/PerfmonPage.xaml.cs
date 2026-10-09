using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Pages;

// ── small VM for each row in the checkbox list ───────────────────────────────
public sealed class CounterItem : INotifyPropertyChanged
{
    private bool _isChecked;
    public string CounterName  { get; init; } = "";
    public string ObjectShort  { get; init; } = "";  // e.g. "SQL Statistics"
    public string DisplayName  => CounterName;

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class PerfmonPage : Page, ICodeBehindPage, IPageContext
{
    // Live, or History while a past range is picked; the range picker is in the top bar (#140).
    public PageKind Kind => _range.IsLive ? PageKind.Live : PageKind.History;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(_intervalSec);
    public bool CanPause => _range.IsLive;
    public bool IsPaused => ViewModel.IsPaused;

    // The bar's Resume and Pause start and stop the reads: the page's own Start and Stop were the
    // same toggle, so they went with the page template (#150). They read on the view model's timer (#169).
    public void SetPaused(bool paused)
    {
        if (paused) StopTimer();
        else if (_range.IsLive) StartTimer();
    }
    public DateTime? DataTo => _range.IsLive ? null : PageBar.DataTo(_historyLoad, b => b.TimeUtc);
    public FrameworkElement? TakeBarTools() => PageBar.Detach(RangePicker);

    // ── Constants ────────────────────────────────────────────────────────────
    private const int MaxPoints  = 60;
    private const int MaxSeries  = 12;

    // ── Counter group definitions (mirrors PM's PerfmonPacks) ────────────────
    private static readonly string[] GroupNames =
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

    // cntr_type = 272696576  →  cumulative rate counter  (must diff)
    // everything else        →  instantaneous / absolute
    private const int RateCntrType = LiveMetricsRepository.PerfmonRateCounterType;

    // ── State ────────────────────────────────────────────────────────────────
    private readonly IWaitStatsRepository _repo;
    private int  _intervalSec;
    private bool _suppressEvents;

    // All known counter items (full master list populated on first load)
    private List<CounterItem>            _allItems    = [];

    // Per-counter rolling data  (key = CounterName)
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _windows = [];
    // Per-counter previous raw value for delta computation
    private readonly Dictionary<string, long>   _prevValues  = [];
    private DateTime _prevTime = DateTime.MinValue;

    private static readonly int[] Intervals =
        Enumerable.Range(1, 24).Select(i => i * 5).ToArray();
    private static string IntervalLabel(int s) =>
        s < 60 ? $"{s}s" : $"{s / 60}m {s % 60:00}s";

    // Past ranges come from the counters recorded with each history detail snapshot. The live
    // buffers above are kept while one is shown, so switching back to Live loses nothing.
    private const string NoLiveDataText = "Select counters from the left panel to begin charting.";
    private const string NotRecordedText =
        "Perfmon counters are recorded with each history snapshot (every few minutes) while SqlVitals " +
        "monitors this connection. Versions before 0.30 didn't record them.";
    private readonly ConnectionHistory? _history;
    private HistoryRange _range = HistoryRange.Live;
    private int _historyVersion;   // bumped per load; a stale load is dropped
    private HistoryLoad<CounterHistoryBucket>? _historyLoad;
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _historyWindows = [];   // built on demand

    // "Compare to baseline" draws the same time last week under each counter, dashed. It is
    // re-clipped to the chart on every rebuild, so it follows the live window as it moves.
    private readonly BaselineTracker<CounterHistoryBucket>? _baseline;
    private string _baselineNote = "";
    private string _statusMain;

    // The page template's verdict (#150): the counters past a known limit, and the ones rising.
    internal PerfmonViewModel ViewModel { get; }

    // The top bar: what this page read, what went wrong, and its kind or window (#166).
    private readonly IShellStatus _shell;

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    /// <param name="navigator">Where the verdict's cause goes; the app's (#182) when null.</param>
    public PerfmonPage(IWaitStatsRepository repo, ConnectionHistory? history = null, ISectionFolds? folds = null,
                       INavigator? navigator = null, IShellStatus? shell = null)
    {
        _shell   = shell ?? CompositionRoot.Get<IShellStatus>();
        _repo    = repo;
        _history = history;
        if (history is not null)
            _baseline = new BaselineTracker<CounterHistoryBucket>(history, (reader, id, from, to, bucket) => reader.ReadCounters(id, from, to, bucket));

        // Before the controls: picking the first group of counters below judges them already, and
        // the interval picked below is its timer's. Not reading until Resume (▶) in the top bar.
        ViewModel = new PerfmonViewModel(navigator ?? CompositionRoot.Get<INavigator>());
        ViewModel.SetPaused(true);
        InitializeComponent();
        RangePicker.SetHistoryAvailable(history is not null, ConnectionHistory.UnavailableReason);
        _statusMain = TxtStatus.Text;

        CmbInterval.ItemsSource   = Intervals.Select(IntervalLabel).ToList();
        CmbInterval.SelectedIndex = 1;   // default 10 s

        CmbGroup.ItemsSource   = GroupNames;
        CmbGroup.SelectedIndex = 0;

        TxtCountdown.SetBinding(TextBlock.TextProperty, new Binding(nameof(ViewModel.Countdown)) { Source = ViewModel });
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        UpdateVerdict();
    }

    // The counters ticked, as the chart draws them, judged against their well-known limits. Before
    // the first read there are none to tick yet: the group picked is what will be charted.
    private void UpdateVerdict()
    {
        var windows  = _range.IsLive ? _windows : _historyWindows;
        var names    = _allItems.Count == 0
            ? Groups[CmbGroup.SelectedItem as string ?? GroupNames[0]].Take(MaxSeries)
            : _allItems.Where(i => i.IsChecked).Take(MaxSeries).Select(i => i.CounterName);
        var selected = names
            .Select(name => new PerfmonSeries(name,
                windows.TryGetValue(name, out var w) ? w.Select(p => p.Value).ToList() : []))
            .ToList();
        var state  = !_range.IsLive ? WaitTrendState.History : !IsPaused ? WaitTrendState.Live : WaitTrendState.NotStarted;
        var noData = !_range.IsLive && _historyLoad is { Items.Count: 0 } load ? load.NoData(NotRecordedText) : null;
        ViewModel.Show(selected, state, noData);
    }

    // ── IRefreshable — manual refresh (also called by timer) ─────────────────
    // A past range is read again, so "last hour" moves up to now.
    public System.Threading.Tasks.Task RefreshAsync() =>
        _range.IsLive ? RefreshLiveAsync() : LoadHistoryAsync();

    private async System.Threading.Tasks.Task RefreshLiveAsync()
    {
        List<SqlVitals.Engine.Models.PerfmonCounter> data;
        try
        {
            data = (await _repo.GetPerfmonCountersAsync()).ToList();
        }
        catch (Exception ex) when (_allItems.Count == 0)
        {
            // Nothing read yet: the verdict says why. Later, the lines so far stay drawn.
            ViewModel.ShowError("the performance counters", ex);
            throw;
        }

        // On first call: build master counter list from whatever the server returns
        if (_allItems.Count == 0)
            PopulateMasterList(data);

        var now     = DateTime.Now;
        double secs = _prevTime == DateTime.MinValue ? 1.0
                      : Math.Max(0.1, (now - _prevTime).TotalSeconds);
        _prevTime   = now;

        // Build a lookup: CounterName → current cntr_value
        var lookup = data.GroupBy(c => c.CounterName)
                         .ToDictionary(g => g.Key, g => g.First().CntrValue);

        foreach (var (name, window) in _windows)
        {
            if (!lookup.TryGetValue(name, out var rawValue)) continue;

            // Determine display value
            double displayVal;
            var item = _allItems.FirstOrDefault(i => i.CounterName == name);
            bool isRate = data.FirstOrDefault(c => c.CounterName == name)?.CntrType == RateCntrType;

            if (isRate)
            {
                if (_prevValues.TryGetValue(name, out var prev))
                    displayVal = Math.Max(0, (rawValue - prev) / secs);
                else
                    displayVal = 0;
            }
            else
            {
                displayVal = rawValue;
            }
            _prevValues[name] = rawValue;

            window.Add(new DateTimePoint(now, displayVal));
            while (window.Count > MaxPoints) window.RemoveAt(0);
        }

        // A read that finishes after a past range was picked only fills the live buffers.
        if (!_range.IsLive)
            return;

        // The baseline is clipped to the live window, which has just moved.
        if (RangePicker.CompareToBaseline)
            RebuildSeries();
        UpdateChart();
        UpdateVerdict();

        ShowStatus($"Last refresh: {now:HH:mm:ss}  •  {data.Count} counters retrieved");
        await LoadBaselineAsync();
    }

    // ── History ──────────────────────────────────────────────────────────────
    // Connected from XAML by name: a generic event handler is attached through reflection.
    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private async void RangePicker_RangeChanged(object? sender, HistoryRange range)
    {
        _range = range;
        var live = range.IsLive;

        // Live refresh is stopped while a past range is shown; Resume picks it up again.
        if (!live)
            StopTimer();
        CmbInterval.IsEnabled    = live;
        _shell.ReportContextChanged(this);

        // A baseline belongs to the range it was read for.
        _baseline?.Clear();
        _baselineNote = "";

        if (live)
        {
            _historyVersion++;
            _historyLoad = null;
            _historyWindows.Clear();
            ShowStatus("Live: Resume (▶) in the top bar starts reading, Refresh reads once.");
            RebuildSeries();
            UpdateChart();
            await LoadBaselineAsync();
            return;
        }

        try
        {
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            // Only the latest pick owns the status line.
            if (_range != range)
                return;
            ShowStatus($"Could not read the history: {ex.Message}");
            _shell.ReportError(this, "Perfmon history", ex, PageRead.Timed);
        }
    }

    private async System.Threading.Tasks.Task LoadHistoryAsync()
    {
        if (_history is null || _range.IsLive)
            return;

        var version = ++_historyVersion;
        TxtStatus.Text = $"Reading {_range.Label.ToLowerInvariant()} from history…";

        var load = await _history.LoadAsync(_range, (reader, id, from, to, bucket) => reader.ReadCounters(id, from, to, bucket));
        if (version != _historyVersion)
            return;   // the user picked something else meanwhile

        _historyLoad = load;
        _historyWindows.Clear();

        // Opened on a past range before any live read: offer the counters that were recorded.
        if (_allItems.Count == 0)
            PopulateMasterList(load.Items.SelectMany(b => b.Values.Keys).Distinct());

        RebuildSeries();
        UpdateChart();
        ShowStatus(load.Items.Count == 0 ? "No history in this range" : load.Describe(load.Items.Select(b => b.TimeUtc).ToList()));
        _shell.ReportContextChanged(this);

        await LoadBaselineAsync();
    }

    // The counter averaged over each bucket, with breaks where nothing was recorded. A bucket
    // without this counter had it at zero: zero, not a break.
    private ObservableCollection<DateTimePoint> BuildHistoryWindow(string counterName)
    {
        var window = new ObservableCollection<DateTimePoint>();
        if (_historyLoad is not { } load)
            return window;

        foreach (var (time, bucket) in OnLocalClock(load.Items, load.Bucket))
            window.Add(new DateTimePoint(time, bucket?.Values.GetValueOrDefault(counterName)));
        return window;
    }

    // History buckets on this PC's clock, as the chart plots them, with a null where the line breaks.
    private static IEnumerable<(DateTime Time, CounterHistoryBucket? Bucket)> OnLocalClock(
        IReadOnlyList<CounterHistoryBucket> items, TimeSpan bucket) =>
        HistoryGaps.WithGaps(items, b => b.TimeUtc, bucket).Select(p => (p.Time.ToLocalTime(), p.Item));

    // ── Baseline ─────────────────────────────────────────────────────────────
    // Connected from XAML by name: a generic event handler is attached through reflection.
    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private async void RangePicker_BaselineChanged(object? sender, bool on)
    {
        _baseline?.Clear();
        _baselineNote = "";
        RebuildSeries();
        UpdateChart();
        ShowStatus(_statusMain);
        await LoadBaselineAsync();
    }

    // Reads the baseline for what the chart shows, when it doesn't have one yet: the past range
    // on screen, or the live window. Live, that is a new read only every half hour or so.
    [ReportsItsErrors]
    private async System.Threading.Tasks.Task LoadBaselineAsync()
    {
        if (_baseline is null || !RangePicker.CompareToBaseline)
            return;

        try
        {
            var read = _range.IsLive
                ? await _baseline.EnsureLiveAsync(LiveSpan())
                : _historyLoad is { } load && await _baseline.LoadAsync(load.FromUtc, load.ToUtc, load.Bucket);
            if (!read || _baseline.Current is not { } baseline)
                return;

            _baselineNote = baseline.Describe();
            RebuildSeries();
            UpdateChart();
            ShowStatus(_statusMain);
        }
        catch (Exception ex)
        {
            _baselineNote = $"Could not read the baseline: {ex.Message}";
            ShowStatus(_statusMain);
            _shell.ReportError(this, "Perfmon baseline", ex, PageRead.Timed);
        }
    }

    // How long the live lines on the chart span.
    private TimeSpan LiveSpan()
    {
        var shown = _windows.Values.Where(w => w.Count > 0).ToList();
        return shown.Count == 0 ? TimeSpan.Zero : shown.Max(w => w[^1].DateTime) - shown.Min(w => w[0].DateTime);
    }

    // Last week's line for one counter, moved onto this week's axis between from and to.
    private static ObservableCollection<DateTimePoint> BuildBaselineWindow(BaselineLoad<CounterHistoryBucket> baseline,
                                                                           string counterName, DateTime from, DateTime to)
    {
        var window = new ObservableCollection<DateTimePoint>();
        foreach (var (time, bucket) in HistoryBaseline.Shift(OnLocalClock(baseline.Items, baseline.Bucket), from, to))
            window.Add(new DateTimePoint(time, bucket?.Values.GetValueOrDefault(counterName)));
        return window;
    }

    // Sets the status line, adding what the baseline shows while it is on.
    private void ShowStatus(string text)
    {
        _statusMain    = text;
        TxtStatus.Text = RangePicker.CompareToBaseline && _baselineNote.Length > 0 ? $"{text}  •  {_baselineNote}" : text;
    }

    // ── Master list builder ──────────────────────────────────────────────────
    private void PopulateMasterList(List<SqlVitals.Engine.Models.PerfmonCounter> data) =>
        PopulateMasterList(data
            .GroupBy(c => c.CounterName)
            .Select(g => new CounterItem { CounterName = g.Key, ObjectShort = ShortObjectName(g.First().ObjectName) }));

    // From the history, which keeps counter names only.
    private void PopulateMasterList(IEnumerable<string> counterNames) =>
        PopulateMasterList(counterNames.Select(n => new CounterItem { CounterName = n }));

    private void PopulateMasterList(IEnumerable<CounterItem> items)
    {
        // Keep only counters that appear in at least one group definition
        var allGroupCounters = Groups.Values
            .SelectMany(g => g)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _allItems = items
            .Where(c => allGroupCounters.Contains(c.CounterName))
            .OrderBy(c => c.CounterName)
            .ToList();

        // Create a rolling window for every counter we know about
        foreach (var item in _allItems)
        {
            if (!_windows.ContainsKey(item.CounterName))
                _windows[item.CounterName] = [];
        }

        // Apply the default group selection (first group)
        ApplyGroupPreset(GroupNames[0]);
    }

    // ── Counter list filter / display ────────────────────────────────────────
    private void RefreshCounterList()
    {
        var search = TxtSearch.Text.Trim();
        var visible = string.IsNullOrEmpty(search)
            ? _allItems
            : _allItems.Where(c => c.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        CounterList.ItemsSource = visible;
    }

    private void ApplyGroupPreset(string groupName)
    {
        if (!Groups.TryGetValue(groupName, out var counters)) return;
        _suppressEvents = true;
        foreach (var item in _allItems)
            item.IsChecked = false;

        int applied = 0;
        foreach (var name in counters)
        {
            var match = _allItems.FirstOrDefault(i =>
                string.Equals(i.CounterName, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && applied < MaxSeries)
            {
                match.IsChecked = true;
                applied++;
            }
        }
        _suppressEvents = false;
        TxtSearch.Text  = "";
        RefreshCounterList();
        RebuildSeries();
    }

    // ── Chart series management ──────────────────────────────────────────────
    // The lines for the counters ticked, and the verdict on them.
    private void RebuildSeries()
    {
        RebuildSeriesCore();
        UpdateVerdict();
    }

    private void RebuildSeriesCore()
    {
        var selected = _allItems.Where(i => i.IsChecked).Take(MaxSeries).ToList();

        // Ensure rolling windows exist for all selected
        var windows = _range.IsLive ? _windows : _historyWindows;
        foreach (var item in selected)
            if (!windows.ContainsKey(item.CounterName))
                windows[item.CounterName] = _range.IsLive ? [] : BuildHistoryWindow(item.CounterName);

        var history = _range.IsLive ? null : _historyLoad;
        if (selected.Count == 0 || history is { Items.Count: 0 })
        {
            TxtNoData.Text = selected.Count == 0 ? NoLiveDataText : history!.NoData(NotRecordedText);
            PerfmonChart.Series    = [];
            TxtNoData.Visibility   = Visibility.Visible;
            return;
        }

        TxtNoData.Visibility = Visibility.Collapsed;

        var series = selected.Select((item, idx) =>
        {
            var color  = ChartTheme.Series(idx);
            var window = windows[item.CounterName];
            return (ISeries)new LineSeries<DateTimePoint>
            {
                Values         = window,
                Name           = item.CounterName,
                Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.8f },
                Fill           = null,
                GeometrySize   = 3,
                GeometryFill   = new SolidColorPaint(color),
                GeometryStroke = null,
                LineSmoothness = 0.3,
            };
        }).ToList();

        // The baseline under each line, over the times the chart shows: the range picked, or the
        // live window so far (so the baseline never stretches the live axis).
        if (RangePicker.CompareToBaseline && _baseline?.Current is { } baseline)
        {
            var live = selected.Select(i => windows[i.CounterName]).Where(w => w.Count > 0).ToList();
            (DateTime From, DateTime To)? shown = history is not null
                ? (history.FromUtc.ToLocalTime(), history.ToUtc.ToLocalTime())
                : live.Count > 0 ? (live.Min(w => w[0].DateTime), live.Max(w => w[^1].DateTime)) : null;

            if (shown is { } s)
                series.AddRange(selected.Select((item, idx) => (ISeries)BaselineSeries.Line(
                    BuildBaselineWindow(baseline, item.CounterName, s.From, s.To), item.CounterName, ChartTheme.Series(idx))));
        }

        PerfmonChart.Series = series;

        // A past range is shown whole, so missing history reads as missing rather than cropped.
        var axisFormat = history is null ? "HH:mm:ss" : ConnectionHistory.AxisFormat(history.Span);
        PerfmonChart.XAxes = new[]
        {
            new DateTimeAxis(TimeSpan.FromSeconds(1), dt => dt.ToString(axisFormat, CultureInfo.CurrentCulture))
            {
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = history?.FromUtc.ToLocalTime().Ticks,
                MaxLimit        = history?.ToUtc.ToLocalTime().Ticks,
            }
        };

        PerfmonChart.YAxes = new[]
        {
            new Axis
            {
                Name            = "Value",
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = 0,
            }
        };

        PerfmonChart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Bottom;
    }

    private void UpdateChart()
    {
        PerfmonChart.CoreChart?.Update(
                new LiveChartsCore.Kernel.ChartUpdateParams
                {
                    IsAutomaticUpdate = false,
                    Throttling        = false,
                });
    }

    private static string ShortObjectName(string full)
    {
        // "SQLServer:SQL Statistics" → "SQL Statistics"
        var idx = full.IndexOf(':');
        return idx >= 0 ? full[(idx + 1)..].Trim() : full.Trim();
    }

    // ── Timer ────────────────────────────────────────────────────────────────
    // What Start did: read every interval, counting down to the next read.
    private void StartTimer()
    {
        if (!IsPaused)
            return;
        _intervalSec = CmbInterval.SelectedIndex >= 0 ? Intervals[CmbInterval.SelectedIndex] : 10;
        ViewModel.ReadEvery(TimeSpan.FromSeconds(_intervalSec), countdown: true);
        ViewModel.SetPaused(false);
        _shell.ReportContextChanged(this);
        UpdateVerdict();
    }

    // What Stop did; leaving the page stops the view model's timer itself.
    private void StopTimer()
    {
        if (IsPaused)
            return;
        ViewModel.SetPaused(true);
        _shell.ReportContextChanged(this);
        UpdateVerdict();
    }

    // ── UI event handlers ────────────────────────────────────────────────────
    // Another interval: the wait for the next read starts again; the countdown shows only while reading.
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedIndex < 0) return;
        _intervalSec = Intervals[CmbInterval.SelectedIndex];
        ViewModel.ReadEvery(TimeSpan.FromSeconds(_intervalSec), countdown: true);
        _shell.ReportContextChanged(this);
    }

    private void CmbGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || CmbGroup.SelectedItem is not string name) return;
        ApplyGroupPreset(name);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        int applied = 0;
        foreach (var item in _allItems)
        {
            item.IsChecked = (applied < MaxSeries);
            if (item.IsChecked) applied++;
        }
        _suppressEvents = false;
        RefreshCounterList();
        RebuildSeries();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        foreach (var item in _allItems) item.IsChecked = false;
        _suppressEvents = false;
        RefreshCounterList();
        RebuildSeries();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshCounterList();

    private void Counter_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        // Enforce max-series limit
        var checked_ = _allItems.Count(i => i.IsChecked);
        if (checked_ > MaxSeries)
        {
            _suppressEvents = true;
            // Uncheck the one that just got checked if we're over the limit
            if (sender is CheckBox cb && cb.DataContext is CounterItem item && item.IsChecked)
                item.IsChecked = false;
            _suppressEvents = false;
            TxtStatus.Text = $"Maximum {MaxSeries} counters — deselect one first.";
            return;
        }

        RebuildSeries();
    }
}
