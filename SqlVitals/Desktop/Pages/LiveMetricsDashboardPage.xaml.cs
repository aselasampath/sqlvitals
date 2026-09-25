using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

public partial class LiveMetricsDashboardPage : Page, IRefreshable
{
    // ?? Constants ??????????????????????????????????????????????????????
    private const int MaxPoints = MonitoringSession.MaxPoints;   // rolling window length

    // ?? Repo & timer ??????????????????????????????????????????????????
    // Collection lives in the connection's MonitoringSession, which keeps running (and keeps
    // its history) while the user is on another page or another connection. The page only
    // plots it; its own timer just drives the countdown.
    private readonly IWaitStatsRepository _repo;
    private readonly MonitoringSession    _session;
    private readonly bool                 _ownsSession;   // ad-hoc session when there is no saved connection
    private readonly DispatcherTimer      _timer    = new();
    private bool                          _itemsReady = false;
    private DateTime?                     _lastPlotted;
    private bool                          _refreshingMap;

    // A past range from the monitoring history replaces the live samples on the charts while
    // it is picked; the session keeps collecting (and recording) meanwhile.
    private readonly ConnectionHistory?   _history;
    private HistoryRange                  _range = HistoryRange.Live;
    private int                           _historyVersion;   // bumped per load; a stale load is dropped
    private string                        _axisFormat = "HH:mm:ss";
    private double?                       _xMin, _xMax;      // the picked range; null while live
    private HistoryLoad<MetricHistoryPoint>? _historyLoad;   // the picked range's read; null while live
    private string                        _historyNote = "";
    private readonly (ObservableCollection<DateTimePoint> Series, Func<LiveMetricSample, double> Value)[] _plotted;

    // "Compare to baseline" draws the same time last week on every chart, dashed: a line per
    // metric, and on the stacked waits chart one line for the total of the categories shown.
    // It is re-clipped to the charts as the live window moves.
    private const int WaitCategories = 6;   // the first entries of _plotted, stacked on the waits chart
    private readonly BaselineTracker<MetricHistoryPoint>? _baseline;
    private readonly ObservableCollection<DateTimePoint>[] _baselineValues;   // parallel to _plotted
    private readonly ObservableCollection<DateTimePoint>   _baselineWaits = [];
    private readonly bool[]               _waitsShown = [true, true, true, true, true, true];
    private string                        _baselineNote = "";

    // Each chart's own series, and its baseline series shown beside them while the baseline is on.
    private readonly Dictionary<string, (Func<ISeries[]> Current, ISeries[] Baseline)> _chartSeries = [];

    private static readonly int[] Intervals =
        Enumerable.Range(1, 24).Select(i => i * 5).ToArray();
    private static string IntervalLabel(int s) =>
        s < 60 ? $"{s}s" : $"{s / 60}m {s % 60:00}s";

    // ?? Previous snapshot for delta-wait calculation ???????????????????

    // ?? Time-series ring buffers ??????????????????????????????????????
    // Waits
    private readonly ObservableCollection<DateTimePoint> _waitCpu     = [];
    private readonly ObservableCollection<DateTimePoint> _waitIo      = [];
    private readonly ObservableCollection<DateTimePoint> _waitLock    = [];
    private readonly ObservableCollection<DateTimePoint> _waitMem     = [];
    private readonly ObservableCollection<DateTimePoint> _waitNet     = [];
    private readonly ObservableCollection<DateTimePoint> _waitOther   = [];
    // CPU
    private readonly ObservableCollection<DateTimePoint> _cpuPct      = [];
    // Throughput
    private readonly ObservableCollection<DateTimePoint> _batches     = [];
    private readonly ObservableCollection<DateTimePoint> _compiles    = [];
    private readonly ObservableCollection<DateTimePoint> _recompiles  = [];
    private readonly ObservableCollection<DateTimePoint> _txns        = [];
    // Physical I/O
    private readonly ObservableCollection<DateTimePoint> _physReads   = [];
    private readonly ObservableCollection<DateTimePoint> _physWrites  = [];
    // Memory
    private readonly ObservableCollection<DateTimePoint> _ple         = [];
    private readonly ObservableCollection<DateTimePoint> _mgPending   = [];
    private readonly ObservableCollection<DateTimePoint> _bufCache    = [];

    // ?? Panel registry: tag ? Border card ????????????????????????????
    private readonly Dictionary<string, Border> _panels = [];
    private readonly Dictionary<string, LiveChartsCore.SkiaSharpView.WPF.CartesianChart> _charts = [];

    // ?? Chip registry: tag ? ToggleButton ????????????????????????????
    private readonly Dictionary<string, ToggleButton> _chips = [];

    // ?? Process Map fields ???????????????????????????????????????????
    private ProcessMapControl? _activeProcessMap;

    // ?? SkiaSharp colours ?????????????????????????????????????????????
    private static readonly SKColor ColCpu   = SKColor.Parse("#FF4444");
    private static readonly SKColor ColIo    = SKColor.Parse("#FFA62B");
    private static readonly SKColor ColLock  = SKColor.Parse("#FF4560");
    private static readonly SKColor ColMem   = SKColor.Parse("#9B59B6");
    private static readonly SKColor ColNet   = SKColor.Parse("#00D9FF");
    private static readonly SKColor ColOther = SKColor.Parse("#26E7A6");
    private static readonly SKColor ColBatch = SKColor.Parse("#26E7A6");
    private static readonly SKColor ColComp  = SKColor.Parse("#FF6B6B");
    private static readonly SKColor ColRec   = SKColor.Parse("#7B68EE");
    private static readonly SKColor ColTxn   = SKColor.Parse("#FFC107");
    private static readonly SKColor ColPhysR = SKColor.Parse("#26E7A6");
    private static readonly SKColor ColPhysW = SKColor.Parse("#FF6B6B");
    private static readonly SKColor ColPle   = SKColor.Parse("#7C3AED");
    private static readonly SKColor ColMgP   = SKColor.Parse("#FF4444");
    private static readonly SKColor ColBuf   = SKColor.Parse("#22C55E");

    // Named in the health score's breakdown.
    private readonly string               _connectionName;

    public LiveMetricsDashboardPage(IWaitStatsRepository repo, MonitoringSession? session = null,
                                    ConnectionHistory? history = null, string? connectionName = null)
    {
        _repo        = repo;
        _connectionName = connectionName ?? "This connection";
        _ownsSession = session is null;
        _session     = session ?? new MonitoringSession(null, repo, string.Empty);
        _history     = history;
        _plotted =
        [
            (_waitCpu,    s => s.WaitCpuMsPerSec),
            (_waitIo,     s => s.WaitIoMsPerSec),
            (_waitLock,   s => s.WaitLockMsPerSec),
            (_waitMem,    s => s.WaitMemoryMsPerSec),
            (_waitNet,    s => s.WaitNetworkMsPerSec),
            (_waitOther,  s => s.WaitOtherMsPerSec),
            (_cpuPct,     s => s.SqlCpuPct),
            (_batches,    s => s.BatchRequestsPerSec),
            (_compiles,   s => s.CompilationsPerSec),
            (_recompiles, s => s.RecompilationsPerSec),
            (_txns,       s => s.TransactionsPerSec),
            (_physReads,  s => s.PhysicalReadsPerSec),
            (_physWrites, s => s.PhysicalWritesPerSec),
            (_ple,        s => s.PageLifeExpectancySec),
            (_mgPending,  s => s.MemoryGrantsPending),
            (_bufCache,   s => s.BufferCacheHitRatio),
        ];
        _baselineValues = _plotted.Select(_ => new ObservableCollection<DateTimePoint>()).ToArray();
        if (history is not null)
            _baseline = new BaselineTracker<MetricHistoryPoint>(history, (reader, id, from, to, bucket) => reader.ReadMetrics(id, from, to, bucket));
        InitializeComponent();
        RangePicker.SetHistoryAvailable(history is not null, ConnectionHistory.UnavailableReason);

        // Populate interval ComboBox with the session's current interval (default 10 s)
        CmbInterval.ItemsSource   = Intervals.Select(IntervalLabel).ToList();
        var index = Array.IndexOf(Intervals, _session.IntervalSeconds);
        CmbInterval.SelectedIndex = index >= 0 ? index : 1;
        _itemsReady = true;

        // Build chip registry from XAML-named buttons
        // (populated in Loaded after InitializeComponent)

        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick    += Timer_Tick;

        // Subscribed here rather than in Loaded: MainWindow calls RefreshAsync straight after
        // navigating, and that sample can arrive before the page has loaded.
        _session.SampleAdded  += Session_SampleAdded;
        _session.StateChanged += Session_StateChanged;
    }

    // ?? Page lifetime ?????????????????????????????????????????????????
    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // Register chips
        _chips["Waits"]      = ChipWaits;
        _chips["Cpu"]        = ChipCpu;
        _chips["Throughput"] = ChipThroughput;
        _chips["PhysicalIO"] = ChipPhysicalIO;
        _chips["Memory"]     = ChipMemory;
        _chips["ProcessMap"] = ChipProcessMap;

        // Build all panels (hidden panels are simply removed from the grid)
        BuildAllPanels();

        // Plot the history collected while this page wasn't open. The new charts draw it when
        // they load, so no forced update here.
        PlotNewSamples();

        // Shared sessions are started by MonitoringManager (and may have been paused by the user).
        if (_ownsSession)
            _session.Start();

        SyncRunButton();
        _timer.Start();
        UpdateCountdown();
        UpdateHealthScore();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();

        // The session outlives the page; don't let it keep this page alive.
        _session.SampleAdded  -= Session_SampleAdded;
        _session.StateChanged -= Session_StateChanged;

        if (_ownsSession)
            _session.Dispose();
    }

    // ?? IRefreshable ??????????????????????????????????????????????????
    // Live: takes a sample now (joining one already in flight), which Session_SampleAdded plots.
    // A past range: reads it again, so "last hour" moves up to now. A failure propagates so
    // MainWindow can report it.
    public System.Threading.Tasks.Task RefreshAsync() =>
        _range.IsLive ? _session.CollectNowAsync() : LoadHistoryAsync();

    private void Session_SampleAdded(MonitoringSession session, LiveMetricSample sample)
    {
        if (_range.IsLive)
        {
            PlotNewSamples();
            PlotBaseline();
            ForceChartUpdate();
            _ = LoadBaselineAsync();
        }
        _ = RefreshProcessMapAsync();
    }

    // Time range
    private async void RangePicker_RangeChanged(object? sender, HistoryRange range)
    {
        _range = range;

        // A baseline belongs to the range it was read for.
        _baseline?.Clear();
        _baselineNote = "";
        PlotBaseline();

        if (range.IsLive)
        {
            ShowLive();
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
            if (_range == range)
            {
                ShowStatus($"Could not read the history: {ex.Message}");
                MainWindow.ReportBackgroundError(this, "Live Metrics history", ex);
            }
        }
    }

    private void ShowLive()
    {
        _historyVersion++;
        _axisFormat  = "HH:mm:ss";
        _xMin = _xMax = null;
        _historyLoad = null;
        ShowStatus("");

        foreach (var (series, _) in _plotted)
            series.Clear();
        _lastPlotted = null;
        PlotNewSamples();
        PlotBaseline();
        ApplyXAxisLimits();
        ForceChartUpdate();
    }

    private async System.Threading.Tasks.Task LoadHistoryAsync()
    {
        if (_history is null || _range.IsLive)
            return;

        var version = ++_historyVersion;
        ShowStatus($"Reading {_range.Label.ToLowerInvariant()} from history…");

        var load = await _history.LoadAsync(_range, (reader, id, from, to, bucket) => reader.ReadMetrics(id, from, to, bucket));
        if (version != _historyVersion)
            return;   // the user picked something else meanwhile

        // On the server's clock, like the live samples, so switching to a past range doesn't
        // shift the axis by the server's time zone. The range's ends use the latest offset.
        var offset = load.Items.Count > 0 ? load.Items[^1].ServerClockOffset : TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        _historyLoad = load;
        _axisFormat  = ConnectionHistory.AxisFormat(load.Span);
        _xMin = (load.FromUtc + offset).Ticks;
        _xMax = (load.ToUtc + offset).Ticks;

        foreach (var (series, _) in _plotted)
            series.Clear();
        foreach (var (time, point) in OnServerClock(load.Items, load.Bucket, offset))
            foreach (var (series, value) in _plotted)
                series.Add(new DateTimePoint(time, point is null ? null : value(point.Averages)));

        ShowStatus(load.Items.Count == 0
            ? load.NoData("Live Metrics samples are recorded while SqlVitals monitors this connection.")
            : load.Describe(load.Items.Select(p => p.TimeUtc).ToList(), "the server's clock"));

        PlotBaseline();
        ApplyXAxisLimits();
        ForceChartUpdate();

        await LoadBaselineAsync();
    }

    // History points on the server's clock, as the live samples are plotted, with a null where
    // the line breaks. Each point uses the server's offset recorded with it, so a daylight-saving
    // change on the server lands where the server saw it.
    private static IEnumerable<(DateTime Time, MetricHistoryPoint? Point)> OnServerClock(
        IReadOnlyList<MetricHistoryPoint> items, TimeSpan bucket, TimeSpan offset)
    {
        foreach (var (time, point) in HistoryGaps.WithGaps(items, p => p.TimeUtc, bucket))
        {
            if (point is not null)
                offset = point.ServerClockOffset;
            yield return (time + offset, point);
        }
    }

    // ── Baseline ────────────────────────────────────────────────────────────
    private async void RangePicker_BaselineChanged(object? sender, bool on)
    {
        _baseline?.Clear();
        _baselineNote = "";
        PlotBaseline();
        foreach (var tag in _chartSeries.Keys)
            ApplySeries(tag);
        ShowStatus(_historyNote);
        ForceChartUpdate();
        await LoadBaselineAsync();
    }

    // Reads the baseline for what the charts show, when they don't have one yet: the past range
    // on screen, or the live window. Live, that is a new read only every half hour or so.
    private async System.Threading.Tasks.Task LoadBaselineAsync()
    {
        if (_baseline is null || !RangePicker.CompareToBaseline)
            return;

        try
        {
            var read = _range.IsLive
                ? await _baseline.EnsureLiveAsync(_cpuPct.Count > 1 ? _cpuPct[^1].DateTime - _cpuPct[0].DateTime : TimeSpan.Zero)
                : _historyLoad is { } load && await _baseline.LoadAsync(load.FromUtc, load.ToUtc, load.Bucket);
            if (!read || _baseline.Current is not { } baseline)
                return;

            _baselineNote = baseline.Describe();
            ShowStatus(_historyNote);
            PlotBaseline();
            ForceChartUpdate();
        }
        catch (Exception ex)
        {
            _baselineNote = $"Could not read the baseline: {ex.Message}";
            ShowStatus(_historyNote);
            MainWindow.ReportBackgroundError(this, "Live Metrics baseline", ex);
        }
    }

    // Fills the baseline series from the baseline read last, over the times the charts show: the
    // range picked, or the live samples so far (so the baseline never stretches the live axis).
    // Empty while the baseline is off.
    private void PlotBaseline()
    {
        foreach (var series in _baselineValues)
            series.Clear();
        _baselineWaits.Clear();

        if (!RangePicker.CompareToBaseline || _baseline?.Current is not { } baseline)
            return;

        DateTime from, to;
        if (_xMin is { } min && _xMax is { } max)
            (from, to) = (new DateTime((long)min), new DateTime((long)max));
        else if (_cpuPct.Count > 0)
            (from, to) = (_cpuPct[0].DateTime, _cpuPct[^1].DateTime);
        else
            return;

        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        foreach (var (time, point) in HistoryBaseline.Shift(OnServerClock(baseline.Items, baseline.Bucket, offset), from, to))
        {
            for (var i = 0; i < _plotted.Length; i++)
                _baselineValues[i].Add(new DateTimePoint(time, point is null ? null : _plotted[i].Value(point.Averages)));

            double? waits = null;
            if (point is not null)
            {
                waits = 0;
                for (var i = 0; i < WaitCategories; i++)
                    if (_waitsShown[i])
                        waits += _plotted[i].Value(point.Averages);
            }
            _baselineWaits.Add(new DateTimePoint(time, waits));
        }
    }

    private ObservableCollection<DateTimePoint> BaselineOf(ObservableCollection<DateTimePoint> series) =>
        _baselineValues[Array.FindIndex(_plotted, p => p.Series == series)];

    // Registers a chart's series and shows them, with its baseline while that is on.
    private void SetSeries(string tag, Func<ISeries[]> current, params ISeries[] baseline)
    {
        _chartSeries[tag] = (current, baseline);
        ApplySeries(tag);
    }

    private void ApplySeries(string tag)
    {
        if (!_charts.TryGetValue(tag, out var chart) || !_chartSeries.TryGetValue(tag, out var series))
            return;
        chart.Series = RangePicker.CompareToBaseline ? [.. series.Current(), .. series.Baseline] : series.Current();
    }

    // The history status line, with what the baseline shows while it is on.
    private void ShowStatus(string historyNote)
    {
        _historyNote = historyNote;
        var baselineNote = RangePicker.CompareToBaseline ? _baselineNote : "";
        TxtHistoryStatus.Text = string.Join(" · ", new[] { historyNote, baselineNote }.Where(s => s.Length > 0));
    }

    // Shows the whole picked range, so missing history reads as missing rather than being cropped.
    private void ApplyXAxisLimits()
    {
        foreach (var chart in _charts.Values)
            foreach (var axis in chart.XAxes)
            {
                axis.MinLimit = _xMin;
                axis.MaxLimit = _xMax;
            }
    }

    private void Session_StateChanged(MonitoringSession session)
    {
        SyncRunButton();
        UpdateCountdown();
        UpdateHealthScore();
    }

    // ── Health score (#36) ──────────────────────────────────────────────────
    // Always the latest sample's, also while a past range is on the charts.
    private void UpdateHealthScore()
    {
        var score = _session.Score;
        var brush = score is null ? HealthBrushes.Grey : HealthBrushes.For(score.Level);
        RunHealthScore.Text        = score?.Value.ToString() ?? "—";
        RunHealthScore.Foreground  = brush;
        BtnHealthScore.BorderBrush = brush;
        BtnHealthScore.Opacity     = _session.IsRunning ? 1 : 0.6;
        BtnHealthScore.ToolTip     = score is null
            ? _session.HealthText
            : $"{score.Summary()}\nClick to see which checks lowered it.";

        if (HealthScorePopup.IsOpen)
            ShowHealthScoreDetails();
    }

    private void ShowHealthScoreDetails() =>
        HealthScoreDetails.Show(_connectionName, _session.Score, _session.HealthText, paused: !_session.IsRunning);

    private void BtnHealthScore_Click(object sender, RoutedEventArgs e)
    {
        // Clicking anywhere else closes it (StaysOpen is false).
        ShowHealthScoreDetails();
        HealthScorePopup.IsOpen = true;
    }

    // Appends every session sample not plotted yet, so the page catches up whether the
    // samples arrived while it was closed or one at a time while it is open.
    private void PlotNewSamples()
    {
        foreach (var sample in _session.Samples)
        {
            if (_lastPlotted is { } last && sample.Time <= last)
                continue;
            AppendSample(sample);
            _lastPlotted = sample.Time;
        }
    }

    private void ForceChartUpdate()
    {
        // Force update charts to fix freeze issue
        foreach (var chart in _charts.Values)
        {
            // CoreChart throws ("Core not set yet") rather than returning null until the chart
            // has loaded — e.g. a panel just built, or a sample arriving before the page loads.
            if (!chart.IsLoaded)
                continue;

            chart.CoreChart.Update(new LiveChartsCore.Kernel.ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
        }
    }

    // The process map is only needed while it is on screen, so it is fetched by the page
    // rather than collected in the background.
    private async System.Threading.Tasks.Task RefreshProcessMapAsync()
    {
        if (!IsLoaded || _refreshingMap || _activeProcessMap == null || ChipProcessMap.IsChecked != true)
            return;

        _refreshingMap = true;
        try
        {
            var nodes = (await _repo.GetProcessesAsync()).ToList();
            _activeProcessMap?.UpdateNodes(nodes);
            MainWindow.ReportBackgroundSuccess(this);
        }
        catch (Exception ex)
        {
            MainWindow.ReportBackgroundError(this, "Live Metrics process map", ex);
        }
        finally
        {
            _refreshingMap = false;
        }
    }

    // ?? Timer ?????????????????????????????????????????????????????????
    private void Timer_Tick(object? sender, EventArgs e) => UpdateCountdown();

    private void UpdateCountdown()
    {
        if (!_session.IsRunning)
        {
            TxtCountdown.Text = "--";
            return;
        }

        var remaining = (int)Math.Ceiling(_session.TimeUntilNextSample.TotalSeconds);
        TxtCountdown.Text = remaining >= 60
            ? $"{remaining / 60}:{remaining % 60:00}"
            : $"{remaining}s";
    }

    // Reflects the session's running state on the Start/Stop button. Its handlers call
    // Start/Pause, which do nothing when the session is already in that state.
    private void SyncRunButton()
    {
        if (BtnAutoRefresh.IsChecked != _session.IsRunning)
            BtnAutoRefresh.IsChecked = _session.IsRunning;
    }

    // ?? Snapshot ? ring buffers ????????????????????????????????????????
    // Rates are computed by LiveMetricSample.From in the session; the page only plots them.
    private void AppendSample(LiveMetricSample s)
    {
        foreach (var (series, value) in _plotted)
        {
            series.Add(new DateTimePoint(s.Time, value(s)));
            while (series.Count > MaxPoints)
                series.RemoveAt(0);
        }
    }

    // ?? Panel builder ?????????????????????????????????????????????????
    private void BuildAllPanels()
    {
        ChartGrid.Children.Clear();
        _panels.Clear();
        _charts.Clear();

        BuildWaitsPanel();
        BuildCpuPanel();
        BuildThroughputPanel();
        BuildPhysicalIoPanel();
        BuildMemoryPanel();
        BuildProcessMapPanel();

        AdjustColumns();
    }

    private void AdjustColumns()
    {
        int visible = ChartGrid.Children.Count;
        ChartGrid.Columns = visible <= 1 ? 1 : 2;
    }

    // ?? Individual panel factories ?????????????????????????????????????
    private void BuildWaitsPanel()
    {
        if (_chips.TryGetValue("Waits", out var chip) && chip.IsChecked != true) return;
        _panels["Waits"] = BuildWaitsPanelDirectWithFilters();
        ChartGrid.Children.Add(_panels["Waits"]);
    }

    private void BuildCpuPanel()
    {
        if (_chips.TryGetValue("Cpu", out var chip) && chip.IsChecked != true) return;
        _panels["Cpu"] = BuildCpuPanelDirect();
        ChartGrid.Children.Add(_panels["Cpu"]);
    }

    private void BuildThroughputPanel()
    {
        if (_chips.TryGetValue("Throughput", out var chip) && chip.IsChecked != true) return;
        _panels["Throughput"] = BuildThroughputPanelDirect();
        ChartGrid.Children.Add(_panels["Throughput"]);
    }

    private void BuildPhysicalIoPanel()
    {
        if (_chips.TryGetValue("PhysicalIO", out var chip) && chip.IsChecked != true) return;
        _panels["PhysicalIO"] = BuildPhysicalIoPanelDirect();
        ChartGrid.Children.Add(_panels["PhysicalIO"]);
    }

    private void BuildMemoryPanel()
    {
        if (_chips.TryGetValue("Memory", out var chip) && chip.IsChecked != true) return;
        _panels["Memory"] = BuildMemoryPanelDirect();
        ChartGrid.Children.Add(_panels["Memory"]);
    }

    private void BuildProcessMapPanel()
    {
        if (_chips.TryGetValue("ProcessMap", out var chip) && chip.IsChecked != true) return;
        _panels["ProcessMap"] = BuildProcessMapPanelDirect();
        ChartGrid.Children.Add(_panels["ProcessMap"]);
    }

    // ?? Close-panel handler ???????????????????????????????????????????
    private void ClosePanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            // Remove from grid
            if (_panels.TryGetValue(tag, out var panel))
            {
                ChartGrid.Children.Remove(panel);
                _panels.Remove(tag);
                _charts.Remove(tag);
            }

            // Uncheck the chip so it reflects closed state
            if (_chips.TryGetValue(tag, out var chip))
                chip.IsChecked = false;

            AdjustColumns();
        }
    }

    // ?? Chip toggle handler ???????????????????????????????????????????
    private void Chip_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;   // guard during InitializeComponent

        if (sender is ToggleButton chip && chip.Tag is string tag)
        {
            // Remove old instance if present
            if (_panels.TryGetValue(tag, out var existing))
            {
                ChartGrid.Children.Remove(existing);
                _panels.Remove(tag);
                _charts.Remove(tag);
            }

            if (chip.IsChecked == true)
            {
                // Re-add in correct order
                int insertIdx = GetInsertIndex(tag);
                Border? newPanel = tag switch
                {
                    "Waits"      => BuildPanelFor("Waits"),
                    "Cpu"        => BuildPanelFor("Cpu"),
                    "Throughput" => BuildPanelFor("Throughput"),
                    "PhysicalIO" => BuildPanelFor("PhysicalIO"),
                    "Memory"     => BuildPanelFor("Memory"),
                    "ProcessMap" => BuildPanelFor("ProcessMap"),
                    _            => null
                };
                if (newPanel is not null)
                {
                    _panels[tag] = newPanel;
                    ChartGrid.Children.Insert(
                        Math.Min(insertIdx, ChartGrid.Children.Count), newPanel);
                }
            }
        }
        AdjustColumns();
    }

    private Border? BuildPanelFor(string tag)
    {
        // Temporarily force chip to checked so the Build method doesn't skip it
        return tag switch
        {
            "Waits"      => BuildWaitsPanelDirectWithFilters(),
            "Cpu"        => BuildCpuPanelDirect(),
            "Throughput" => BuildThroughputPanelDirect(),
            "PhysicalIO" => BuildPhysicalIoPanelDirect(),
            "Memory"     => BuildMemoryPanelDirect(),
            "ProcessMap" => BuildProcessMapPanelDirect(),
            _            => null
        };
    }

    // Direct builders (no chip-check guard) used by Chip_Changed ??????
    private Border BuildWaitsPanelDirect()
    {
        var chart = MakeCartesian();
        var sCpu   = StackedArea(_waitCpu,   "CPU",     ColCpu);
        var sIo    = StackedArea(_waitIo,    "I/O",     ColIo);
        var sLock  = StackedArea(_waitLock,  "Locks",   ColLock);
        var sMem   = StackedArea(_waitMem,   "Memory",  ColMem);
        var sNet   = StackedArea(_waitNet,   "Network", ColNet);
        var sOther = StackedArea(_waitOther, "Other",   ColOther);
        
        chart.Series = new ISeries[] { sCpu, sIo, sLock, sMem, sNet, sOther };
        SetDateTimeAxes(chart, "Wait (ms/s)");

        var filters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,10,0) };
        CheckBox AddFilter(string label, ISeries series, bool isChecked = true)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                Margin = new Thickness(4,0,0,0),
                Foreground = Brushes.WhiteSmoke,
                FontSize = 10,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // Use Unchecked/Checked events to toggle visibility
            void Toggle(object s, RoutedEventArgs e) => series.IsVisible = cb.IsChecked == true;
            cb.Checked += Toggle;
            cb.Unchecked += Toggle;
            filters.Children.Add(cb);
            return cb;
        }

        AddFilter("CPU", sCpu);
        AddFilter("I/O", sIo);
        AddFilter("Locks", sLock);
        AddFilter("Mem", sMem);
        AddFilter("Net", sNet);
        AddFilter("Other", sOther); // default checked, user can uncheck

        return WrapPanel("Waits - Stack Area", chart, "Waits", filters);
    }

    private Border BuildCpuPanelDirect()
    {
        var chart = MakeCartesian();
        _charts["Cpu"] = chart;
        var series = new ISeries[] { Line(_cpuPct, "SQL Server CPU", ColCpu) };
        SetSeries("Cpu", () => series, BaselineLine(_cpuPct, "SQL Server CPU", ColCpu));
        SetDateTimeAxes(chart, "% CPU", 0, 100);
        return WrapPanel("CPU Usage", chart, "Cpu");
    }

    private Border BuildThroughputPanelDirect()
    {
        var chart = MakeCartesian();
        _charts["Throughput"] = chart;
        var series = new ISeries[]
        {
            Line(_batches,    "Batch Requests",  ColBatch),
            Line(_compiles,   "Compilations",    ColComp),
            Line(_recompiles, "Recompilations",  ColRec),
            Line(_txns,       "Transactions",    ColTxn),
        };
        SetSeries("Throughput", () => series,
            BaselineLine(_batches,    "Batch Requests",  ColBatch),
            BaselineLine(_compiles,   "Compilations",    ColComp),
            BaselineLine(_recompiles, "Recompilations",  ColRec),
            BaselineLine(_txns,       "Transactions",    ColTxn));
        SetDateTimeAxes(chart, "# per sec.");
        return WrapPanel("SQL Throughput", chart, "Throughput");
    }

    private Border BuildPhysicalIoPanelDirect()
    {
        var chart = MakeCartesian();
        _charts["PhysicalIO"] = chart;
        var series = new ISeries[]
        {
            Line(_physReads,  "Physical Reads",  ColPhysR),
            Line(_physWrites, "Physical Writes", ColPhysW),
        };
        SetSeries("PhysicalIO", () => series,
            BaselineLine(_physReads,  "Physical Reads",  ColPhysR),
            BaselineLine(_physWrites, "Physical Writes", ColPhysW));
        SetDateTimeAxes(chart, "R/W per sec.");
        return WrapPanel("Physical R/W", chart, "PhysicalIO");
    }

    private Border BuildMemoryPanelDirect()
    {
        var chart = MakeCartesian();
        _charts["Memory"] = chart;
        
        // 1. Configure Series with Axis assignments
        var series = new ISeries[]
        {
            // Primary Axis (Index 0)
            Line(_mgPending, "Mem Grants Pending",     ColMgP, 0),
            Line(_bufCache,  "Buffer Cache Hit Ratio", ColBuf, 0),

            // Secondary Axis (Index 1) for PLE
            Line(_ple, "Page Life Expectancy", ColPle, 1),
        };
        SetSeries("Memory", () => series,
            BaselineLine(_mgPending, "Mem Grants Pending",     ColMgP, 0),
            BaselineLine(_bufCache,  "Buffer Cache Hit Ratio", ColBuf, 0),
            BaselineLine(_ple,       "Page Life Expectancy",   ColPle, 1));

        // 2. Configure X-Axis (Time)
        chart.XAxes = new[] { TimeAxis() };

        // 3. Configure Y-Axes
        chart.YAxes = new[]
        {
            // Axis 0: Count / %
            new Axis
            {
                Name            = "Count / %",
                NamePaint       = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 14,
                Position        = LiveChartsCore.Measure.AxisPosition.Start
            },
            // Axis 1: PLE � format label as h:mm or Xd depending on magnitude
            new Axis
            {
                Name            = "PLE (sec)",
                NamePaint       = new SolidColorPaint(ColPle) { IsAntialias = true },
                LabelsPaint     = new SolidColorPaint(ColPle) { IsAntialias = true },
                SeparatorsPaint = null, // Hide grid lines to avoid clutter
                TextSize        = 14,
                Position        = LiveChartsCore.Measure.AxisPosition.End,
                Labeler         = v =>
                {
                    if (v < 0) return "0s";
                    if (v < 3600)   return $"{v:N0}s";
                    if (v < 86400)  return $"{v / 3600:N1}h";
                    return $"{v / 86400:N1}d";
                }
            }
        };

        return WrapPanel("Memory", chart, "Memory");
    }

    private Border BuildProcessMapPanelDirect()
    {
        _activeProcessMap = new ProcessMapControl();
        return WrapPanel("Process Map", _activeProcessMap, "ProcessMap");
    }

    // Compute insertion index to preserve panel order ??????????????????
    private static readonly string[] PanelOrder = ["Waits","Cpu","Throughput","PhysicalIO","Memory","ProcessMap"];

    private int GetInsertIndex(string tag)
    {
        int targetRank = Array.IndexOf(PanelOrder, tag);
        int idx = 0;
        foreach (var key in PanelOrder)
        {
            if (key == tag) break;
            if (_panels.ContainsKey(key) && ChartGrid.Children.Contains(_panels[key]))
                idx++;
        }
        return idx;
    }

    // ?? PLE formatting helper ?????????????????????????????????????????
    private static string FormatPle(double seconds)
    {
        if (seconds < 0)     return "0s";
        if (seconds < 3600)  return $"{seconds:N0}s";
        if (seconds < 86400) return $"{seconds / 3600:N1}h";
        return $"{seconds / 86400:N1}d";
    }

    // ?? LiveCharts helpers ????????????????????????????????????????????
    private static LiveChartsCore.SkiaSharpView.WPF.CartesianChart MakeCartesian() =>
        new()
        {
            TooltipPosition = LiveChartsCore.Measure.TooltipPosition.Top,
            LegendPosition  = LiveChartsCore.Measure.LegendPosition.Bottom,
            Background      = System.Windows.Media.Brushes.Transparent,
            AnimationsSpeed = TimeSpan.Zero,
        };

    private static StackedAreaSeries<DateTimePoint> StackedArea(
        ObservableCollection<DateTimePoint> values, string name, SKColor color) =>
        new()
        {
            Values          = values,
            Name            = name,
            Fill            = new SolidColorPaint(color.WithAlpha(160)),
            Stroke          = new SolidColorPaint(color) { StrokeThickness = 1.5f },
            GeometrySize    = 0,
            LineSmoothness  = 0.4,
        };

    // The baseline under one of the plotted series.
    private ISeries BaselineLine(ObservableCollection<DateTimePoint> series, string name, SKColor color, int scalesAt = 0) =>
        BaselineSeries.Line(BaselineOf(series), name, color, scalesAt);

    private static LineSeries<DateTimePoint> Line(
        ObservableCollection<DateTimePoint> values, string name, SKColor color, int scalesAt = 0) =>
        new()
        {
            Values         = values,
            Name           = name,
            Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.8f },
            Fill           = null,
            GeometrySize   = 4,
            GeometryFill   = new SolidColorPaint(color),
            GeometryStroke = null,
            LineSmoothness = 0.3,
            ScalesYAt      = scalesAt,
        };

    // Labels follow the range on screen: seconds while live, dates on ranges over a day.
    private DateTimeAxis TimeAxis() =>
        new(TimeSpan.FromSeconds(1), dt => dt.ToString(_axisFormat))
        {
            LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
            SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
            TextSize        = 14,
            MinLimit        = _xMin,
            MaxLimit        = _xMax,
        };

    private void SetDateTimeAxes(
        LiveChartsCore.SkiaSharpView.WPF.CartesianChart chart,
        string yLabel,
        double? yMin = null, double? yMax = null)
    {
        chart.XAxes = new[] { TimeAxis() };
        var yAxis = new Axis
        {
            Name            = yLabel,
            NamePaint       = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
            LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
            SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
            TextSize        = 14,
        };
        if (yMin.HasValue) yAxis.MinLimit = yMin;
        if (yMax.HasValue) yAxis.MaxLimit = yMax;
        chart.YAxes = new[] { yAxis };
    }

    // ?? Chart card wrapper with title + close button ??????????????????
    private Border WrapPanel(string title, FrameworkElement chart, string tag, FrameworkElement? extraHeader = null)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var titleBlock = new TextBlock
        {
            Text       = title,
            Foreground = ChartTheme.PanelTitleColor,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(titleBlock, Dock.Left);
        header.Children.Add(titleBlock);

        var closeBtn = new Button
        {
            Content = "x",
            Tag     = tag,
            Style   = (Style)FindResource("CloseButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(closeBtn, Dock.Right);
        closeBtn.Click += ClosePanel_Click;
        header.Children.Add(closeBtn);

        if (extraHeader != null)
        {
            DockPanel.SetDock(extraHeader, Dock.Right);
            header.Children.Add(extraHeader);
        }

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(header, 0);
        inner.Children.Add(header);

        Grid.SetRow(chart, 1);
        inner.Children.Add(chart);

        return new Border
        {
            Style   = (Style)Application.Current.FindResource("KpiCard"),
            Margin  = new Thickness(4),
            Child   = inner,
        };
    }

    // ?? Toolbar handlers ??????????????????????????????????????????????
    // Sets this connection's collection interval — it applies in the background too.
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_itemsReady || CmbInterval.SelectedIndex < 0) return;
        _session.IntervalSeconds = Intervals[CmbInterval.SelectedIndex];
        UpdateCountdown();
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content    = "Stop";
        BtnAutoRefresh.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40));
        BtnAutoRefresh.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40));
        BtnAutoRefresh.Background  = new SolidColorBrush(Color.FromArgb(0x22, 0xC0, 0x60, 0x00));
        _session.Start();
        UpdateCountdown();
    }

    private void BtnAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content    = "Start";
        BtnAutoRefresh.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xDD, 0x66));
        BtnAutoRefresh.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0xAA, 0x44));
        BtnAutoRefresh.Background  = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x3A, 0x1E));

        // Pausing stops collection for this connection entirely, including in the background.
        _session.Pause();
        UpdateCountdown();
    }

    private Border BuildWaitsPanelDirectWithFilters()
    {
        var chart = MakeCartesian();
        _charts["Waits"] = chart;
        var sCpu   = StackedArea(_waitCpu,   "CPU",     ColCpu);
        var sIo    = StackedArea(_waitIo,    "I/O",     ColIo);
        var sLock  = StackedArea(_waitLock,  "Locks",   ColLock);
        var sMem   = StackedArea(_waitMem,   "Memory",  ColMem);
        var sNet   = StackedArea(_waitNet,   "Network", ColNet);
        var sOther = StackedArea(_waitOther, "Other",   ColOther);

        // Keep definition order for stacking
        var definitions = new[]
        {
            (Series: (ISeries)sCpu, Label: "CPU"),
            (Series: (ISeries)sIo, Label: "I/O"),
            (Series: (ISeries)sLock, Label: "Locks"),
            (Series: (ISeries)sMem, Label: "Mem"),
            (Series: (ISeries)sNet, Label: "Net"),
            (Series: (ISeries)sOther, Label: "Other")
        };

        var filters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,10,0) };
        var checkBoxes = new List<CheckBox>();

        // Initial set: every category. The baseline is one line against the top of the stack:
        // last week's total of the categories shown.
        Array.Fill(_waitsShown, true);
        PlotBaseline();
        SetSeries("Waits",
            () => definitions.Where((_, i) => _waitsShown[i]).Select(x => x.Series).ToArray(),
            BaselineSeries.Line(_baselineWaits, "Total waits", ChartTheme.AxisColor));
        SetDateTimeAxes(chart, "Wait (ms/s)");

        void RebuildSeries(object? sender, RoutedEventArgs e)
        {
            for (int i = 0; i < checkBoxes.Count; i++)
                _waitsShown[i] = checkBoxes[i].IsChecked == true;
            PlotBaseline();
            ApplySeries("Waits");
            // Force update
            if (chart.IsLoaded)
                chart.CoreChart.Update(new LiveChartsCore.Kernel.ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
        }

        foreach (var def in definitions)
        {
            var cb = new CheckBox
            {
                Content = def.Label,
                IsChecked = true,
                Margin = new Thickness(6,0,0,0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA,0xBB,0xCC)),
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            cb.Checked += RebuildSeries;
            cb.Unchecked += RebuildSeries;
            checkBoxes.Add(cb);
            filters.Children.Add(cb);
        }

        return WrapPanel("Waits - Stack Area", chart, "Waits", filters);
    }
}
