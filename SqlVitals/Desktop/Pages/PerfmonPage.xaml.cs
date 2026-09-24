using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Repositories;

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

public partial class PerfmonPage : Page, IRefreshable
{
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
    private const int RateCntrType = WaitStatsRepository.PerfmonRateCounterType;

    // Colour palette for series lines
    private static readonly SKColor[] Palette =
    [
        SKColor.Parse("#26E7A6"),
        SKColor.Parse("#FF6B6B"),
        SKColor.Parse("#7C3AED"),
        SKColor.Parse("#FFA62B"),
        SKColor.Parse("#00D9FF"),
        SKColor.Parse("#FF4560"),
        SKColor.Parse("#FFC107"),
        SKColor.Parse("#9B59B6"),
        SKColor.Parse("#1ABC9C"),
        SKColor.Parse("#E74C3C"),
        SKColor.Parse("#3498DB"),
        SKColor.Parse("#F39C12"),
    ];

    // ── State ────────────────────────────────────────────────────────────────
    private readonly IWaitStatsRepository _repo;
    private readonly DispatcherTimer      _timer       = new();
    private int  _intervalSec;
    private int  _remainingSec;
    private bool _refreshing;
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

    public PerfmonPage(IWaitStatsRepository repo, ConnectionHistory? history = null)
    {
        _repo    = repo;
        _history = history;
        InitializeComponent();
        RangePicker.SetHistoryAvailable(history is not null, ConnectionHistory.UnavailableReason);

        CmbInterval.ItemsSource   = Intervals.Select(IntervalLabel).ToList();
        CmbInterval.SelectedIndex = 1;   // default 10 s

        CmbGroup.ItemsSource   = GroupNames;
        CmbGroup.SelectedIndex = 0;

        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick    += Timer_Tick;
    }

    // ── Page lifetime ────────────────────────────────────────────────────────
    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (NavigationService != null)
            NavigationService.Navigating += OnFrameNavigating;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (NavigationService != null)
            NavigationService.Navigating -= OnFrameNavigating;
        StopTimer();
    }

    private void OnFrameNavigating(object sender,
        System.Windows.Navigation.NavigatingCancelEventArgs e) => StopTimer();

    // ── IRefreshable — manual refresh (also called by timer) ─────────────────
    // A past range is read again, so "last hour" moves up to now.
    public System.Threading.Tasks.Task RefreshAsync() =>
        _range.IsLive ? RefreshLiveAsync() : LoadHistoryAsync();

    private async System.Threading.Tasks.Task RefreshLiveAsync()
    {
        var data = (await _repo.GetPerfmonCountersAsync()).ToList();

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

        UpdateChart();

        TxtStatus.Text = $"Last refresh: {now:HH:mm:ss}  •  {data.Count} counters retrieved";
    }

    // ── History ──────────────────────────────────────────────────────────────
    private async void RangePicker_RangeChanged(object? sender, HistoryRange range)
    {
        _range = range;
        var live = range.IsLive;

        // Live refresh is stopped while a past range is shown; Start picks it up again.
        if (!live)
            StopTimer();
        BtnAutoRefresh.IsEnabled = live;
        CmbInterval.IsEnabled    = live;

        if (live)
        {
            _historyVersion++;
            _historyLoad = null;
            _historyWindows.Clear();
            TxtStatus.Text = "Live — press Start or use manual Refresh.";
            RebuildSeries();
            UpdateChart();
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
            TxtStatus.Text = $"Could not read the history: {ex.Message}";
            MainWindow.ReportBackgroundError(this, "Perfmon history", ex);
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
        TxtStatus.Text = load.Items.Count == 0 ? "No history in this range" : load.Describe(load.Items.Select(b => b.TimeUtc).ToList());
    }

    // The counter averaged over each bucket, with breaks where nothing was recorded. A bucket
    // without this counter had it at zero: zero, not a break.
    private ObservableCollection<DateTimePoint> BuildHistoryWindow(string counterName)
    {
        var window = new ObservableCollection<DateTimePoint>();
        if (_historyLoad is not { } load)
            return window;

        foreach (var (time, bucket) in HistoryGaps.WithGaps(load.Items, b => b.TimeUtc, load.Bucket))
            window.Add(new DateTimePoint(time.ToLocalTime(),
                bucket is null ? null : bucket.Values.GetValueOrDefault(counterName)));
        return window;
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
    private void RebuildSeries()
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
            var color  = Palette[idx % Palette.Length];
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
        }).ToArray();

        PerfmonChart.Series = series;

        // A past range is shown whole, so missing history reads as missing rather than cropped.
        var axisFormat = history is null ? "HH:mm:ss" : ConnectionHistory.AxisFormat(history.Span);
        PerfmonChart.XAxes = new[]
        {
            new DateTimeAxis(TimeSpan.FromSeconds(1), dt => dt.ToString(axisFormat))
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
        if (PerfmonChart.CoreChart is not null)
        {
            PerfmonChart.CoreChart.Update(
                new LiveChartsCore.Kernel.ChartUpdateParams
                {
                    IsAutomaticUpdate = false,
                    Throttling        = false,
                });
        }
    }

    private static string ShortObjectName(string full)
    {
        // "SQLServer:SQL Statistics" → "SQL Statistics"
        var idx = full.IndexOf(':');
        return idx >= 0 ? full[(idx + 1)..].Trim() : full.Trim();
    }

    // ── Timer ────────────────────────────────────────────────────────────────
    private void StartTimer()
    {
        _remainingSec = _intervalSec;
        UpdateCountdown();
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer.Stop();
        TxtCountdown.Text = "--";
        if (BtnAutoRefresh.IsChecked == true)
        {
            BtnAutoRefresh.IsChecked = false;
            BtnAutoRefresh.Content   = "Start";
        }
    }

    private void UpdateCountdown() =>
        TxtCountdown.Text = $"{_remainingSec}s";

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSec--;
        UpdateCountdown();
        if (_remainingSec > 0 || _refreshing) return;

        _refreshing = true;
        try
        {
            await RefreshAsync();
            MainWindow.ReportBackgroundSuccess(this);
        }
        catch (Exception ex)
        {
            MainWindow.ReportBackgroundError(this, "Perfmon", ex);
        }
        finally
        {
            _refreshing   = false;
            _remainingSec = _intervalSec;
        }
    }

    // ── UI event handlers ────────────────────────────────────────────────────
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedIndex < 0) return;
        _intervalSec  = Intervals[CmbInterval.SelectedIndex];
        _remainingSec = _intervalSec;
        UpdateCountdown();
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Stop";
        _intervalSec = CmbInterval.SelectedIndex >= 0
            ? Intervals[CmbInterval.SelectedIndex] : 10;
        StartTimer();
    }

    private void BtnAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Start";
        StopTimer();
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
