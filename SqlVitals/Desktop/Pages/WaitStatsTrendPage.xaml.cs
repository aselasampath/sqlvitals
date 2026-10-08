using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Pages;

// ── View-model for each wait type checkbox row ────────────────────────────────
public sealed class WaitTypeItem : INotifyPropertyChanged
{
    public string WaitType     { get; }
    public string WaitCategory { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public WaitTypeItem(string waitType, string waitCategory)
    {
        WaitType     = waitType;
        WaitCategory = waitCategory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// ── Page ──────────────────────────────────────────────────────────────────────
public partial class WaitStatsTrendPage : Page, ICodeBehindPage, IPageContext
{
    // Live, or History while a past range is picked; the range picker is in the top bar (#140).
    public PageKind Kind => _range.IsLive ? PageKind.Live : PageKind.History;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(_intervalSec);
    public bool CanPause => _range.IsLive;
    public bool IsPaused => !_timer.IsEnabled;

    // The bar's Resume and Pause start and stop the reads: the page's own Start and Stop were the
    // same toggle, so they went with the page template (#149).
    public void SetPaused(bool paused)
    {
        if (paused) StopReading();
        else if (_range.IsLive) StartReading();
    }
    public DateTime? DataTo => _range.IsLive ? null : PageBar.DataTo(_historyLoad, b => b.TimeUtc);
    public FrameworkElement? TakeBarTools() => PageBar.Detach(RangePicker);

    // ── constants ────────────────────────────────────────────────────────────
    private const int MaxPoints = 60;
    private const int MaxSeries = 12;

    // ── fields ────────────────────────────────────────────────────────────────
    private readonly IWaitStatsRepository _repo;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _intervalSec = 10;
    private int _countdown;

    // Wait type list (all items, used as the single source of truth for IsChecked)
    private readonly List<WaitTypeItem> _allItems = new();

    // Rolling window buffers: waitType → ordered time-series points
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _windows = new();

    // Previous snapshot for delta calculation
    private Dictionary<string, (long WaitTimeMs, long SignalWaitTimeMs, long WaitingTasksCount)> _prevValues = new();
    private DateTime _prevTime = DateTime.MinValue;
    private bool _initialized;

    private enum MetricType { WaitTimeMs, SignalWaitMs, ResourceWaitMs, WaitingTasks }
    private MetricType _metric = MetricType.WaitTimeMs;

    // Past ranges come from the history's detail snapshots (waits per type, every few minutes).
    // The live buffers above are kept while one is shown, so switching back to Live loses nothing.
    private const string NoLiveDataText = "Select wait types on the left, then press Resume (▶) in the top bar to begin trending";
    private const string NotRecordedText =
        "Waits per type are recorded every few minutes while SqlVitals monitors this connection (Settings → Monitoring & history).";
    private readonly ConnectionHistory? _history;
    private HistoryRange _range = HistoryRange.Live;
    private int _historyVersion;   // bumped per load; a stale load is dropped
    private HistoryLoad<WaitHistoryBucket>? _historyLoad;
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _historyWindows = new();   // built on demand

    // "Compare to baseline" draws the same time last week under each wait type, dashed. It is
    // re-clipped to the chart on every rebuild, so it follows the live window as it moves.
    private readonly BaselineTracker<WaitHistoryBucket>? _baseline;
    private string _baselineNote = "";
    private string _statusMain   = "";

    // The page template's verdict (#149): the wait type that waits the most on the chart, and
    // whether it is rising. Null until InitializeComponent has run.
    internal WaitStatsTrendViewModel ViewModel { get; }

    // ── constructor ───────────────────────────────────────────────────────────
    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    /// <param name="navigator">Where the verdict's Top Waits goes; the app's (#182) when null.</param>
    public WaitStatsTrendPage(IWaitStatsRepository repo, ConnectionHistory? history = null, ISectionFolds? folds = null,
                              INavigator? navigator = null)
    {
        _repo    = repo;
        _history = history;
        if (history is not null)
            _baseline = new BaselineTracker<WaitHistoryBucket>(history, (reader, id, from, to, bucket) => reader.ReadWaits(id, from, to, bucket));
        InitializeComponent();
        RangePicker.SetHistoryAvailable(history is not null, ConnectionHistory.UnavailableReason);
        _statusMain  = TxtStatus.Text;
        _timer.Tick += Timer_Tick;

        ViewModel = new WaitStatsTrendViewModel(navigator ?? CompositionRoot.Get<INavigator>());
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        UpdateVerdict();
    }

    // ── verdict ───────────────────────────────────────────────────────────────
    // The lines on the chart, as the chart has them: the wait types ticked (at most MaxSeries).
    private void UpdateVerdict()
    {
        if (ViewModel is null)
            return;
        // Nothing read yet: nothing to judge, as on the pilots before their first read.
        if (_allItems.Count == 0)
        {
            ViewModel.Verdict = null;
            return;
        }
        var windows = _range.IsLive ? _windows : _historyWindows;
        var series = _allItems.Where(i => i.IsChecked).Take(MaxSeries)
            .Select(i => new WaitTrendSeries(i.WaitType,
                windows.TryGetValue(i.WaitType, out var points) ? points.Select(p => p.Value).ToList() : []))
            .ToList();
        var state = !_range.IsLive ? WaitTrendState.History : _timer.IsEnabled ? WaitTrendState.Live : WaitTrendState.NotStarted;
        var unit  = _metric == MetricType.WaitingTasks ? "tasks/s" : "ms/s";
        string? noData = _range.IsLive || _historyLoad is null ? null : _historyLoad.NoData(NotRecordedText);
        ViewModel.Show(series, state, unit, Over(windows), noData);
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

    // ── IRefreshable ──────────────────────────────────────────────────────────
    // A past range is read again, so "last hour" moves up to now.
    public async Task RefreshAsync()
    {
        if (_range.IsLive)
            await LoadSnapshotAsync();
        else
            await LoadHistoryAsync();
    }

    // ── core data load ───────────────────────────────────────────────────────
    private async Task LoadSnapshotAsync()
    {
        try
        {
            TxtStatus.Text = "Querying sys.dm_os_wait_stats…";
            var data = (await _repo.GetCumulativeWaitsAsync()).ToList();
            var now  = DateTime.Now;

            if (!_initialized)
            {
                PopulateWaitTypeList(data);
                StoreValues(data, now);
                _initialized = true;
                ShowStatus($"Loaded {_allItems.Count} wait types. Select types, then press Resume (▶) in the top bar.");
                UpdateVerdict();
                return; // first tick — no delta yet
            }

            double elapsedSec = Math.Max(0.1, (now - _prevTime).TotalSeconds);

            // Append delta values to each window
            foreach (var w in data)
            {
                if (!_prevValues.TryGetValue(w.WaitType, out var prev)) continue;

                if (!_windows.TryGetValue(w.WaitType, out var window))
                {
                    window = new ObservableCollection<DateTimePoint>();
                    _windows[w.WaitType] = window;
                }

                double value = _metric switch
                {
                    MetricType.WaitTimeMs    => (w.WaitTimeMs    - prev.WaitTimeMs)    / elapsedSec,
                    MetricType.SignalWaitMs  => (w.SignalWaitTimeMs - prev.SignalWaitTimeMs) / elapsedSec,
                    MetricType.ResourceWaitMs => ((w.WaitTimeMs - w.SignalWaitTimeMs)
                                                 - (prev.WaitTimeMs - prev.SignalWaitTimeMs)) / elapsedSec,
                    MetricType.WaitingTasks  => (w.WaitingTasksCount - prev.WaitingTasksCount) / elapsedSec,
                    _ => 0d
                };

                // Guard against negative values (can occur on server restart)
                value = Math.Max(0d, value);

                window.Add(new DateTimePoint(now, value));
                if (window.Count > MaxPoints) window.RemoveAt(0);
            }

            StoreValues(data, now);
            MainWindow.ReportBackgroundSuccess(this);

            // A read that finishes after a past range was picked only fills the live buffers.
            if (!_range.IsLive)
                return;

            RebuildSeries();

            int checkedCount = _allItems.Count(i => i.IsChecked);
            ShowStatus($"Updated {now:HH:mm:ss}  |  {checkedCount} series shown");
            await LoadBaselineAsync();
        }
        catch (Exception ex)
        {
            if (_range.IsLive)
            {
                ShowStatus($"Error: {ex.Message}");
                ViewModel.ShowError("the wait statistics", ex);
            }
        }
    }

    // ── history ──────────────────────────────────────────────────────────────
    // Connected from XAML by name: a generic event handler is attached through reflection.
    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private async void RangePicker_RangeChanged(object? sender, HistoryRange range)
    {
        _range = range;
        var live = range.IsLive;

        // Live collection is paused while a past range is shown; Resume picks it up again.
        if (!live)
            StopReading();
        CmbInterval.IsEnabled    = live;
        MainWindow.ReportContextChanged(this);

        // A baseline belongs to the range it was read for.
        _baseline?.Clear();
        _baselineNote = "";

        if (live)
        {
            _historyVersion++;
            _historyLoad = null;
            _historyWindows.Clear();
            ShowStatus("Live — press Resume (▶) in the top bar to trend");
            RebuildSeries();
            await LoadBaselineAsync();
            return;
        }

        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        if (_history is null || _range.IsLive)
            return;

        var version = ++_historyVersion;
        TxtStatus.Text = $"Reading {_range.Label.ToLowerInvariant()} from history…";
        try
        {
            var load = await _history.LoadAsync(_range, (reader, id, from, to, bucket) => reader.ReadWaits(id, from, to, bucket));
            if (version != _historyVersion)
                return;   // the user picked something else meanwhile

            _historyLoad = load;
            _historyWindows.Clear();

            // Add waits the list lacks: it was opened on a past range before any live read, or the
            // range has waits the server no longer lists.
            var recorded = load.Items.SelectMany(b => b.Waits.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var known    = _allItems.Select(i => i.WaitType).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var type in recorded.Where(t => !known.Contains(t)).Order())
                _allItems.Add(new WaitTypeItem(type, ""));

            // The live list starts on the top waits since the server started, mostly idle ones the
            // history leaves out. Rather than a chart of flat zeros, start on the range's top waits.
            if (recorded.Count > 0 && !_allItems.Any(i => i.IsChecked && recorded.Contains(i.WaitType)))
                CheckTopWaits();

            UpdateListDisplay(TxtSearch.Text);
            UpdateSelCount();

            RebuildSeries();
            ShowStatus(load.Items.Count == 0 ? "No history in this range" : load.Describe(load.Items.Select(b => b.TimeUtc).ToList()));
            MainWindow.ReportBackgroundSuccess(this);
            MainWindow.ReportContextChanged(this);
        }
        catch (Exception ex)
        {
            if (version != _historyVersion)
                return;
            ShowStatus($"Could not read the history: {ex.Message}");
            ViewModel.ShowError("the monitoring history", ex);
            MainWindow.ReportBackgroundError(this, "Wait Stats Trend history", ex);
            return;
        }

        await LoadBaselineAsync();
    }

    // The chosen metric per second over each bucket, with breaks where nothing was recorded.
    private ObservableCollection<DateTimePoint> BuildHistoryWindow(string waitType)
    {
        var window = new ObservableCollection<DateTimePoint>();
        if (_historyLoad is not { } load)
            return window;

        foreach (var (time, bucket) in OnLocalClock(load.Items, load.Bucket))
            window.Add(new DateTimePoint(time, bucket is null ? null : ValueOf(bucket, waitType)));
        return window;
    }

    // History buckets on this PC's clock, as the chart plots them, with a null where the line breaks.
    private static IEnumerable<(DateTime Time, WaitHistoryBucket? Bucket)> OnLocalClock(
        IReadOnlyList<WaitHistoryBucket> items, TimeSpan bucket) =>
        HistoryGaps.WithGaps(items, b => b.TimeUtc, bucket).Select(p => (p.Time.ToLocalTime(), p.Item));

    // The chosen metric per second over one bucket. A bucket without this wait type had none of
    // it: zero, not a break.
    private double ValueOf(WaitHistoryBucket bucket, string waitType)
    {
        bucket.Waits.TryGetValue(waitType, out var w);
        long total = w is null ? 0 : _metric switch
        {
            MetricType.SignalWaitMs   => w.SignalWaitTimeMs,
            MetricType.ResourceWaitMs => w.WaitTimeMs - w.SignalWaitTimeMs,
            MetricType.WaitingTasks   => w.WaitingTasks,
            _                         => w.WaitTimeMs,
        };
        return bucket.Seconds > 0 ? Math.Max(0, total) / bucket.Seconds : 0;
    }

    // ── baseline ─────────────────────────────────────────────────────────────
    // Connected from XAML by name: a generic event handler is attached through reflection.
    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private async void RangePicker_BaselineChanged(object? sender, bool on)
    {
        _baseline?.Clear();
        _baselineNote = "";
        RebuildSeries();
        ShowStatus(_statusMain);
        await LoadBaselineAsync();
    }

    // Reads the baseline for what the chart shows, when it doesn't have one yet: the past range
    // on screen, or the live window. Live, that is a new read only every half hour or so.
    private async Task LoadBaselineAsync()
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
            ShowStatus(_statusMain);
        }
        catch (Exception ex)
        {
            _baselineNote = $"Could not read the baseline: {ex.Message}";
            ShowStatus(_statusMain);
            MainWindow.ReportBackgroundError(this, "Wait Stats Trend baseline", ex);
        }
    }

    // How long the live lines on the chart span.
    private TimeSpan LiveSpan()
    {
        var shown = _windows.Values.Where(w => w.Count > 0).ToList();
        return shown.Count == 0 ? TimeSpan.Zero : shown.Max(w => w[^1].DateTime) - shown.Min(w => w[0].DateTime);
    }

    // Last week's line for one wait type, moved onto this week's axis between from and to.
    private ObservableCollection<DateTimePoint> BuildBaselineWindow(BaselineLoad<WaitHistoryBucket> baseline,
                                                                    string waitType, DateTime from, DateTime to)
    {
        var window = new ObservableCollection<DateTimePoint>();
        foreach (var (time, bucket) in HistoryBaseline.Shift(OnLocalClock(baseline.Items, baseline.Bucket), from, to))
            window.Add(new DateTimePoint(time, bucket is null ? null : ValueOf(bucket, waitType)));
        return window;
    }

    // Sets the status line, adding what the baseline shows while it is on.
    private void ShowStatus(string text)
    {
        _statusMain    = text;
        TxtStatus.Text = RangePicker.CompareToBaseline && _baselineNote.Length > 0 ? $"{text}  |  {_baselineNote}" : text;
    }

    // Top 20 by total wait time: over the range on screen, or since the server started when live.
    private void CheckTopWaits()
    {
        IEnumerable<string> ranked = _historyLoad is { } load && !_range.IsLive
            ? load.Items.SelectMany(b => b.Waits.Values)
                  .GroupBy(w => w.WaitType)
                  .OrderByDescending(g => g.Sum(w => w.WaitTimeMs))
                  .Select(g => g.Key)
            : _prevValues.OrderByDescending(kv => kv.Value.WaitTimeMs).Select(kv => kv.Key);

        var top20 = ranked.Take(20).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allItems)
            item.IsChecked = top20.Contains(item.WaitType);
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private void PopulateWaitTypeList(List<WaitStatCumulative> data)
    {
        _allItems.Clear();
        foreach (var w in data)
            _allItems.Add(new WaitTypeItem(w.WaitType, w.WaitCategory));

        // Default: check top 20 by current cumulative wait time
        var top20 = data
            .OrderByDescending(w => w.WaitTimeMs)
            .Take(20)
            .Select(w => w.WaitType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _allItems)
            item.IsChecked = top20.Contains(item.WaitType);

        UpdateListDisplay();
        UpdateSelCount();
    }

    private void StoreValues(List<WaitStatCumulative> data, DateTime captureTime)
    {
        _prevValues = data.ToDictionary(
            w => w.WaitType,
            w => (w.WaitTimeMs, w.SignalWaitTimeMs, w.WaitingTasksCount));
        _prevTime = captureTime;
    }

    private void UpdateListDisplay(string filter = "")
    {
        IEnumerable<WaitTypeItem> items = string.IsNullOrWhiteSpace(filter)
            ? _allItems
            : _allItems.Where(i => i.WaitType.Contains(filter, StringComparison.OrdinalIgnoreCase));

        WaitTypeList.ItemsSource = items.ToList();
    }

    private void UpdateSelCount()
    {
        int sel   = _allItems.Count(i => i.IsChecked);
        int total = _allItems.Count;
        TxtSelCount.Text = $"{sel} / {total} selected";
    }

    private void RebuildSeries()
    {
        // Guard: controls may not be ready if called during InitializeComponent
        if (TxtMaxNote is null || TrendChart is null || TxtNoData is null) return;

        var toShow = _allItems.Where(i => i.IsChecked).Take(MaxSeries).ToList();

        TxtMaxNote.Text = _allItems.Count(i => i.IsChecked) > MaxSeries
            ? $"⚠  Max {MaxSeries} series — first {MaxSeries} shown"
            : "";

        var hasData = _range.IsLive ? _windows.Count > 0 : _historyLoad is { Items.Count: > 0 };
        if (toShow.Count == 0 || !hasData)
        {
            TxtNoData.Text = _range.IsLive || _historyLoad is null ? NoLiveDataText
                           : toShow.Count == 0 ? "Select wait types on the left to chart them."
                           : _historyLoad.NoData(NotRecordedText);
            TrendChart.Series = Array.Empty<ISeries>();
            TxtNoData.Visibility = Visibility.Visible;
            UpdateVerdict();
            return;
        }

        TxtNoData.Visibility = Visibility.Collapsed;

        string yLabel = CmbMetric.SelectedIndex switch
        {
            0 => "ms / sec",
            1 => "ms / sec",
            2 => "ms / sec",
            3 => "tasks / sec",
            _ => ""
        };

        var windows = _range.IsLive ? _windows : _historyWindows;
        var buffers = toShow.Select(item =>
        {
            if (!windows.TryGetValue(item.WaitType, out var buf))
            {
                buf = _range.IsLive ? new ObservableCollection<DateTimePoint>() : BuildHistoryWindow(item.WaitType);
                windows[item.WaitType] = buf;
            }
            return buf;
        }).ToList();

        var series = toShow.Select((item, idx) =>
        {
            var colour = ChartTheme.Series(idx);
            return (ISeries)new LineSeries<DateTimePoint>
            {
                Values         = buffers[idx],
                Name           = item.WaitType,
                Stroke         = new SolidColorPaint(colour) { StrokeThickness = 2 },
                Fill           = null,
                GeometrySize   = 0,
                GeometryStroke = null,
                GeometryFill   = null,
                LineSmoothness = 0,
            };
        }).ToList();

        var axisColor = ChartTheme.MutedAxisColor;
        var gridColor = ChartTheme.GridColor;

        // A past range is shown whole, so missing history reads as missing rather than cropped.
        var history    = _range.IsLive ? null : _historyLoad;
        var axisFormat = history is null ? "HH:mm:ss" : ConnectionHistory.AxisFormat(history.Span);

        // The baseline under each line, over the times the chart shows: the range picked, or the
        // live window so far (so the baseline never stretches the live axis).
        if (RangePicker.CompareToBaseline && _baseline?.Current is { } baseline)
        {
            var live = buffers.Where(b => b.Count > 0).ToList();
            (DateTime From, DateTime To)? shown = history is not null
                ? (history.FromUtc.ToLocalTime(), history.ToUtc.ToLocalTime())
                : live.Count > 0 ? (live.Min(b => b[0].DateTime), live.Max(b => b[^1].DateTime)) : null;

            if (shown is { } s)
                series.AddRange(toShow.Select((item, idx) => (ISeries)BaselineSeries.Line(
                    BuildBaselineWindow(baseline, item.WaitType, s.From, s.To), item.WaitType, ChartTheme.Series(idx))));
        }

        TrendChart.Series = series;
        TrendChart.XAxes  = new[]
        {
            new DateTimeAxis(TimeSpan.FromSeconds(1), dt => dt.ToString(axisFormat, CultureInfo.CurrentCulture))
            {
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 10,
                MinLimit        = history?.FromUtc.ToLocalTime().Ticks,
                MaxLimit        = history?.ToUtc.ToLocalTime().Ticks,
            }
        };
        TrendChart.YAxes = new[]
        {
            new Axis
            {
                Name            = yLabel,
                NamePaint       = new SolidColorPaint(axisColor),
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = 0,
            }
        };

        // Force LiveCharts to repaint immediately
        TrendChart.CoreChart?.Update(
            new LiveChartsCore.Kernel.ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
        UpdateVerdict();
    }

    // ── timer ─────────────────────────────────────────────────────────────────
    private void Timer_Tick(object? sender, EventArgs e)
    {
        _countdown--;
        TxtCountdown.Text = $"{_countdown}s";
        if (_countdown <= 0)
        {
            _countdown = _intervalSec;
            _ = LoadSnapshotAsync();
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    // ── event handlers ────────────────────────────────────────────────────────
    private void BtnTopWaits_Click(object sender, RoutedEventArgs e)
    {
        if (_range.IsLive ? !_initialized : _historyLoad is null) return;

        CheckTopWaits();

        UpdateSelCount();
        RebuildSeries();
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allItems) item.IsChecked = false;
        UpdateSelCount();
        RebuildSeries();
    }

    private void WaitType_CheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateSelCount();
        RebuildSeries();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateListDisplay(TxtSearch.Text);

    private void CmbMetric_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _metric = CmbMetric.SelectedIndex switch
        {
            0 => MetricType.WaitTimeMs,
            1 => MetricType.SignalWaitMs,
            2 => MetricType.ResourceWaitMs,
            3 => MetricType.WaitingTasks,
            _ => MetricType.WaitTimeMs,
        };

        // Clear windows so chart restarts with fresh deltas for the chosen metric; history
        // windows are rebuilt from the range already read.
        _windows.Clear();
        _historyWindows.Clear();
        RebuildSeries();
    }

    private int SelectedIntervalSec => CmbInterval.SelectedIndex switch
    {
        0 => 5, 1 => 10, 2 => 30, 3 => 60, _ => 10
    };

    // What Start did: read every interval, counting down to the next read.
    private void StartReading()
    {
        if (_timer.IsEnabled)
            return;
        _intervalSec = SelectedIntervalSec;
        _countdown = _intervalSec;
        TxtCountdown.Text = $"{_countdown}s";
        _timer.Start();
        MainWindow.ReportContextChanged(this);
        UpdateVerdict();
    }

    // What Stop did.
    private void StopReading()
    {
        if (!_timer.IsEnabled)
            return;
        TxtCountdown.Text = "--";
        _timer.Stop();
        MainWindow.ReportContextChanged(this);
        UpdateVerdict();
    }

    // Another interval: used from the next read on, while reading.
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtCountdown is null)
            return;   // during InitializeComponent
        _intervalSec = SelectedIntervalSec;
        if (_timer.IsEnabled)
        {
            _countdown = Math.Min(_countdown, _intervalSec);
            TxtCountdown.Text = $"{_countdown}s";
        }
        MainWindow.ReportContextChanged(this);
    }
}
