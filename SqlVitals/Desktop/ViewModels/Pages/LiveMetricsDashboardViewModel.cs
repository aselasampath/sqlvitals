using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore.Defaults;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Live charts (#189): the connection's monitoring samples plotted as they arrive, or a past range
/// from the monitoring history, with last week under them while Compare to baseline is on; its
/// health score; whether it is monitored, and Start or Stop; how often it is sampled; and the
/// process map while it is shown. Collection lives in the connection's <see cref="MonitoringSession"/>,
/// which keeps running while the page isn't open; the page only plots it. While the connection
/// isn't monitored there is no session and nothing is sent to the server (#115).
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "A page's view model isn't IDisposable; OnLeaving disposes _adHoc")]
public sealed partial class LiveMetricsDashboardViewModel : PageViewModel
{
    /// <summary>What is plotted: the wait categories stacked on the waits chart first, then a line each.</summary>
    public enum Metric
    {
        WaitCpu, WaitIo, WaitLock, WaitMemory, WaitNetwork, WaitOther,
        Cpu, Batches, Compilations, Recompilations, Transactions, PhysicalReads, PhysicalWrites,
        PageLifeExpectancy, GrantsPending, BufferCacheHitRatio,
    }

    /// <summary>The charts, in their order on the page.</summary>
    public enum Chart { Waits, Cpu, Throughput, PhysicalIo, Memory, ProcessMap }

    private const int MaxPoints     = MonitoringSession.MaxPoints;   // the rolling window
    private const int WaitCategories = 6;                             // the first metrics, stacked

    private static readonly Func<LiveMetricSample, double>[] Values =
    [
        s => s.WaitCpuMsPerSec, s => s.WaitIoMsPerSec, s => s.WaitLockMsPerSec, s => s.WaitMemoryMsPerSec,
        s => s.WaitNetworkMsPerSec, s => s.WaitOtherMsPerSec,
        s => s.SqlCpuPct, s => s.BatchRequestsPerSec, s => s.CompilationsPerSec, s => s.RecompilationsPerSec,
        s => s.TransactionsPerSec, s => s.PhysicalReadsPerSec, s => s.PhysicalWritesPerSec,
        s => s.PageLifeExpectancySec, s => s.MemoryGrantsPending, s => s.BufferCacheHitRatio,
    ];

    private static readonly int[] Intervals = Enumerable.Range(1, 24).Select(i => i * 5).ToArray();

    private static readonly string[] WaitNames = ["CPU", "I/O", "Locks", "Mem", "Net", "Other"];

    private readonly IWaitStatsRepository _repo;
    private readonly MonitoringManager?   _monitoring;
    private readonly Guid?                _connectionId;
    private readonly MonitoringSession?   _adHoc;          // when there is no saved connection
    private MonitoringSession?            _session;        // null while not monitored
    private readonly ConnectionHistory?   _history;
    private readonly IWatchStarter?       _starter;
    private readonly INavigator           _navigator;
    private readonly string               _connectionName;

    private readonly ObservableCollection<DateTimePoint>[] _points   = Values.Select(_ => new ObservableCollection<DateTimePoint>()).ToArray();
    private readonly ObservableCollection<DateTimePoint>[] _baseline = Values.Select(_ => new ObservableCollection<DateTimePoint>()).ToArray();

    private DateTime? _lastPlotted;
    private bool      _refreshingMap;
    private bool      _shown;
    private bool      _adHocStarted;

    // A past range from the monitoring history replaces the live samples on the charts while it is
    // picked; the session keeps collecting (and recording) meanwhile.
    private HistoryRange _range = HistoryRange.Live;
    private int          _historyVersion;   // bumped per load; a stale load is dropped
    private HistoryLoad<MetricHistoryPoint>? _historyLoad;
    private string       _historyNote = "";

    // "Compare to baseline" draws the same time last week on every chart, dashed: a line per metric,
    // and on the stacked waits chart one line for the total of the categories shown. It is
    // re-clipped to the charts as the live window moves.
    private readonly BaselineTracker<MetricHistoryPoint>? _baselines;
    private string _baselineNote = "";

    /// <param name="repo">The active connection's: an ad-hoc session's server, and the process map.</param>
    /// <param name="monitoring">Owns the saved connections' sessions, and starts and stops them; null in some tests.</param>
    /// <param name="connectionId">The active saved connection; null for one from appsettings.json only.</param>
    /// <param name="history">Its monitoring history; null when it can't be read.</param>
    /// <param name="connectionName">Named in the health score's breakdown.</param>
    /// <param name="navigator">Where Start monitoring automatically… goes: Settings.</param>
    /// <param name="starter">What Start does for a saved connection (#172).</param>
    public LiveMetricsDashboardViewModel(IWaitStatsRepository repo, MonitoringManager? monitoring, Guid? connectionId, ConnectionHistory? history,
                                         string? connectionName, INavigator navigator, IWatchStarter? starter = null)
    {
        _repo           = repo;
        _monitoring     = monitoring;
        _connectionId   = connectionId;
        _history        = history;
        _navigator      = navigator;
        _connectionName = connectionName ?? "This connection";
        _starter        = starter ?? (monitoring is null ? null : new DirectWatchStarter(monitoring));
        if (connectionId is null)
            _adHoc = new MonitoringSession(null, repo, string.Empty);
        _session = _adHoc ?? monitoring?.Get(connectionId);
        if (history is not null)
            _baselines = new BaselineTracker<MetricHistoryPoint>(history, (reader, id, from, to, bucket) => reader.ReadMetrics(id, from, to, bucket));

        IntervalNames  = Intervals.Select(IntervalLabel).ToList();
        var index      = Array.IndexOf(Intervals, _adHoc?.IntervalSeconds ?? _monitoring?.IntervalFor(connectionId) ?? MonitoringSession.DefaultIntervalSeconds);
        _intervalIndex = index >= 0 ? index : 1;

        WaitsShown = WaitNames
            // Other starts unticked: the catch-all band would dwarf the categories that point at a cause.
            .Select((name, i) => new WaitCategory(name, shown: i < WaitCategories - 1))
            .ToList();
        foreach (var category in WaitsShown)
            category.PropertyChanged += OnWaitShownChanged;

        WhileShownEvery(TimeSpan.FromSeconds(1), ShowCountdown);
        PropertyChanged += OnOwnPropertyChanged;
        ShowMonitoring();
    }

    // ── What is plotted ───────────────────────────────────────────────────

    /// <summary>A metric's points: the live samples, or the range picked. The chart follows the collection.</summary>
    public ObservableCollection<DateTimePoint> Points(Metric metric) => _points[(int)metric];

    /// <summary>Last week's line under a metric, while Compare to baseline is on; empty otherwise.</summary>
    public ObservableCollection<DateTimePoint> Baseline(Metric metric) => _baseline[(int)metric];

    /// <summary>Last week's total of the wait categories shown, against the top of the stack.</summary>
    public ObservableCollection<DateTimePoint> BaselineWaits { get; } = [];

    /// <summary>A wait category on the stacked chart, ticked to show it.</summary>
    public sealed partial class WaitCategory(string name, bool shown) : ObservableObject
    {
        public string Name { get; } = name;

        [ObservableProperty]
        private bool _isShown = shown;
    }

    /// <summary>CPU, I/O, Locks, Mem, Net, Other: which are stacked.</summary>
    public IReadOnlyList<WaitCategory> WaitsShown { get; }

    /// <summary>Points were added, or the categories shown changed: the charts draw it now, without being built again.</summary>
    public event Action? Sampled;

    /// <summary>The time axis: its labels, and the range picked (on the server's clock); null ends follow the samples.</summary>
    public sealed record TimeAxis(string Format, double? MinTicks, double? MaxTicks);

    [ObservableProperty]
    private TimeAxis _axis = new("HH:mm:ss", null, null);

    /// <summary>What the range shows and what the baseline is, in one line.</summary>
    [ObservableProperty]
    private string _historyStatus = string.Empty;

    // Appends every session sample not plotted yet, so the page catches up whether the samples
    // arrived while it was closed or one at a time while it is open.
    private void PlotNewSamples()
    {
        foreach (var sample in _session?.Samples ?? [])
        {
            if (_lastPlotted is { } last && sample.Time <= last)
                continue;
            for (var i = 0; i < Values.Length; i++)
            {
                var series = _points[i];
                series.Add(new DateTimePoint(sample.Time, Values[i](sample)));
                while (series.Count > MaxPoints)
                    series.RemoveAt(0);
            }
            _lastPlotted = sample.Time;
        }
    }

    private void ShowNewSamples()
    {
        PlotNewSamples();
        PlotBaseline();
        Sampled?.Invoke();
        _ = LoadBaselineAsync();
    }

    // ── Reading ───────────────────────────────────────────────────────────

    /// <summary>
    /// Live: takes a sample now (joining one already in flight), which is plotted as it arrives;
    /// nothing while monitoring is off, so Refresh sends nothing to the server. Paused, Refresh
    /// still shows the sample it took, once. A past range: reads it again, so "last hour" moves up to now.
    /// </summary>
    protected override async Task LoadAsync(CancellationToken cancel)
    {
        if (!_range.IsLive)
            await LoadHistoryAsync();
        else if (IsMonitoring)
        {
            await _session!.CollectNowAsync();
            if (IsPaused)
                ShowNewSamples();
        }
    }

    // A failure is said on the top bar; the charts have no verdict.
    protected override void ShowLoadError(Exception exception) { }

    protected override void OnShowing()
    {
        _shown = true;
        Subscribe(_session);

        // The history collected while this page wasn't open; not over a past range picked before it opened (#145).
        if (_range.IsLive)
        {
            PlotNewSamples();
            Sampled?.Invoke();
        }

        // Shared sessions are started by MonitoringManager. The ad-hoc one follows Start monitoring
        // automatically like them, and otherwise waits for Start.
        if (_adHoc is not null && !_adHocStarted && (_monitoring?.StartsAutomatically ?? true))
        {
            _adHocStarted = true;
            _adHoc.Start();
        }

        // Started or stopped elsewhere (selector, tray) from now on, or since the page was made.
        if (_monitoring is not null && _connectionId is { } id)
        {
            _monitoring.SessionStateChanged += OnMonitoringSessionChanged;
            OnMonitoringSessionChanged(id);   // shows Start or Stop
        }
        else
            ShowMonitoring();
    }

    // A saved connection's session outlives the page: it mustn't keep the page alive. An ad-hoc
    // one is the page's own, and ends with it (a page is made again when it is opened again).
    protected override void OnLeaving()
    {
        _shown = false;
        Unsubscribe(_session);
        _monitoring?.SessionStateChanged -= OnMonitoringSessionChanged;
        _adHoc?.Dispose();
    }

    private void Subscribe(MonitoringSession? session)
    {
        if (session is null) return;
        session.SampleAdded  += OnSampleAdded;
        session.StateChanged += OnSessionStateChanged;
    }

    private void Unsubscribe(MonitoringSession? session)
    {
        if (session is null) return;
        session.SampleAdded  -= OnSampleAdded;
        session.StateChanged -= OnSessionStateChanged;
    }

    // Start or Stop here, in the selector or in the tray: the manager made or ended the session.
    private void OnMonitoringSessionChanged(Guid connectionId)
    {
        if (connectionId != _connectionId || _monitoring is null)
            return;

        var session = _monitoring.Get(connectionId);
        if (!ReferenceEquals(session, _session))
        {
            Unsubscribe(_session);
            _session = session;
            if (_shown)
                Subscribe(_session);
            _lastPlotted = null;
            if (_range.IsLive && _session is not null)
                ShowLive();
        }
        OnSessionStateChanged(_session);
    }

    private void OnSampleAdded(MonitoringSession session, LiveMetricSample sample)
    {
        if (IsPaused)
            return;

        if (_range.IsLive)
        {
            ShowNewSamples();
            ReportUpdated();
        }
        MapRead = RefreshProcessMapAsync();
    }

    // Shows Start or Stop and the score again (ShowMonitoring), and the countdown.
    private void OnSessionStateChanged(MonitoringSession? session)
    {
        ShowMonitoring();
        ShowCountdown();
        ReportContextChanged();
    }

    // ── The range picked, and the baseline ────────────────────────────────

    /// <summary>The monitoring history can be read: the range picker offers past ranges.</summary>
    public bool HistoryAvailable => _history is not null;

    /// <summary>Live, or a past range from the monitoring history.</summary>
    public bool IsLive => _range.IsLive;

    /// <summary>The newest data a past range shows, on this PC's clock; null live.</summary>
    public DateTime? DataTo => _range.IsLive ? null : PageBar.DataTo(_historyLoad, p => p.TimeUtc);

    /// <summary>
    /// A range was picked in the top bar: a past one is read from the history; Live plots the
    /// session's samples again. A failure says so here and on the top bar.
    /// </summary>
    public Task PickRangeAsync(HistoryRange range)
    {
        _range = range;
        OnPropertyChanged(nameof(IsLive));
        ReportContextChanged();

        // A baseline belongs to the range it was read for.
        _baselines?.Clear();
        _baselineNote = "";
        PlotBaseline();

        if (range.IsLive)
        {
            ShowLive();
            return LoadBaselineAsync();
        }
        return ReadRangeAsync(range);
    }

    private async Task ReadRangeAsync(HistoryRange range)
    {
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
                ReportSideError("Live Metrics history", ex);
            }
        }
    }

    private void ShowLive()
    {
        _historyVersion++;
        _historyLoad = null;
        Axis = new TimeAxis("HH:mm:ss", null, null);
        ShowStatus("");

        foreach (var series in _points)
            series.Clear();
        _lastPlotted = null;
        PlotNewSamples();
        PlotBaseline();
        Sampled?.Invoke();
    }

    private async Task LoadHistoryAsync()
    {
        if (_history is null || _range.IsLive)
            return;

        var version = ++_historyVersion;
        ShowStatus($"Reading {_range.Label.ToLowerInvariant()} from history…");

        var load = await _history.LoadAsync(_range, (reader, id, from, to, bucket) => reader.ReadMetrics(id, from, to, bucket));
        if (version != _historyVersion)
            return;   // the user picked something else meanwhile

        // On the server's clock, like the live samples, so switching to a past range doesn't shift
        // the axis by the server's time zone. The range's ends use the latest offset.
        var offset = load.Items.Count > 0 ? load.Items[^1].ServerClockOffset : TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        _historyLoad = load;
        Axis = new TimeAxis(ConnectionHistory.AxisFormat(load.Span), (load.FromUtc + offset).Ticks, (load.ToUtc + offset).Ticks);

        foreach (var series in _points)
            series.Clear();
        foreach (var (time, point) in OnServerClock(load.Items, load.Bucket, offset))
            for (var i = 0; i < Values.Length; i++)
                _points[i].Add(new DateTimePoint(time, point is null ? null : Values[i](point.Averages)));

        ShowStatus(load.Items.Count == 0
            ? load.NoData("Live Metrics samples are recorded while SqlVitals monitors this connection.")
            : load.Describe(load.Items.Select(p => p.TimeUtc).ToList(), "the server's clock"));
        ReportContextChanged();

        PlotBaseline();
        Sampled?.Invoke();

        await LoadBaselineAsync();
    }

    // History points on the server's clock, as the live samples are plotted, with a null where the
    // line breaks. Each point uses the server's offset recorded with it, so a daylight-saving change
    // on the server lands where the server saw it.
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

    /// <summary>Compare to baseline in the top bar: last week dashed on every chart.</summary>
    [ObservableProperty]
    private bool _compareToBaseline;

    partial void OnCompareToBaselineChanged(bool value)
    {
        _baselines?.Clear();
        _baselineNote = "";
        PlotBaseline();
        ShowStatus(_historyNote);
        Sampled?.Invoke();
        _ = LoadBaselineAsync();
    }

    /// <summary>The latest read of the baseline; for tests.</summary>
    internal Task BaselineLoaded { get; private set; } = Task.CompletedTask;

    // Reads the baseline for what the charts show, when they don't have one yet: the past range on
    // screen, or the live window. Live, that is a new read only every half hour or so.
    private Task LoadBaselineAsync() => BaselineLoaded = ReadBaselineAsync();

    private async Task ReadBaselineAsync()
    {
        if (_baselines is null || !CompareToBaseline)
            return;

        try
        {
            var cpu  = _points[(int)Metric.Cpu];
            var read = _range.IsLive
                ? await _baselines.EnsureLiveAsync(cpu.Count > 1 ? cpu[^1].DateTime - cpu[0].DateTime : TimeSpan.Zero)
                : _historyLoad is { } load && await _baselines.LoadAsync(load.FromUtc, load.ToUtc, load.Bucket);
            if (!read || _baselines.Current is not { } baseline)
                return;

            _baselineNote = baseline.Describe();
            ShowStatus(_historyNote);
            PlotBaseline();
            Sampled?.Invoke();
        }
        catch (Exception ex)
        {
            _baselineNote = $"Could not read the baseline: {ex.Message}";
            ShowStatus(_historyNote);
            ReportSideError("Live Metrics baseline", ex);
        }
    }

    // Fills the baseline from the one read last, over the times the charts show: the range picked,
    // or the live samples so far (so the baseline never stretches the live axis). Empty while off.
    private void PlotBaseline()
    {
        foreach (var series in _baseline)
            series.Clear();
        BaselineWaits.Clear();

        if (!CompareToBaseline || _baselines?.Current is not { } baseline)
            return;

        var cpu = _points[(int)Metric.Cpu];
        DateTime from, to;
        if (Axis is { MinTicks: { } min, MaxTicks: { } max })
            (from, to) = (new DateTime((long)min), new DateTime((long)max));
        else if (cpu.Count > 0)
            (from, to) = (cpu[0].DateTime, cpu[^1].DateTime);
        else
            return;

        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        foreach (var (time, point) in HistoryBaseline.Shift(OnServerClock(baseline.Items, baseline.Bucket, offset), from, to))
        {
            for (var i = 0; i < Values.Length; i++)
                _baseline[i].Add(new DateTimePoint(time, point is null ? null : Values[i](point.Averages)));

            double? waits = null;
            if (point is not null)
            {
                waits = 0;
                for (var i = 0; i < WaitCategories; i++)
                    if (WaitsShown[i].IsShown)
                        waits += Values[i](point.Averages);
            }
            BaselineWaits.Add(new DateTimePoint(time, waits));
        }
    }

    // A category ticked or unticked: the baseline's total follows, and the stack is drawn again.
    private void OnWaitShownChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WaitCategory.IsShown))
            return;
        PlotBaseline();
        WaitsChanged?.Invoke();
    }

    /// <summary>The wait categories shown changed: the stacked chart takes its series again.</summary>
    public event Action? WaitsChanged;

    // The history status line, with what the baseline shows while it is on.
    private void ShowStatus(string historyNote)
    {
        _historyNote = historyNote;
        var baselineNote = CompareToBaseline ? _baselineNote : "";
        HistoryStatus = string.Join(" · ", new[] { historyNote, baselineNote }.Where(s => s.Length > 0));
    }

    // ── An alert's window (#145) ──────────────────────────────────────────
    // An alert that has ended opens the charts on its window, read from the monitoring history,
    // until Show all (or another range) goes back to live.

    private HistoryRange? _focusRange;

    /// <summary>The window an alert's link opens the charts on, before they load; null when it can't (no history).</summary>
    public HistoryRange? FocusRange(PageFocus focus)
    {
        if (focus.FromUtc is not { } from || _history is null)
            return null;
        var to = focus.ToUtc ?? DateTime.UtcNow;
        return to <= from ? null : HistoryRange.Custom(from, to);
    }

    /// <summary>The range picker took the alert's window: the first load reads it instead of plotting live samples.</summary>
    public void ShowFocus(HistoryRange range) => _range = _focusRange = range;

    /// <summary>Show all: back to live, if the alert's window was on.</summary>
    public bool ClearFocus()
    {
        if (_focusRange is null)
            return false;
        _focusRange = null;
        return true;
    }

    public bool ShowsWindow => _focusRange is not null;

    public string? FocusNote => _focusRange is { } range && _range == range ? $"showing {range.Label} from the monitoring history" : null;

    public string? ShowAllText => _focusRange is { } range && _range == range ? "Show live" : null;

    // ── Paused (the top bar), and monitoring ──────────────────────────────

    /// <summary>The connection is monitored: samples arrive.</summary>
    public bool IsMonitoring => _session is { IsRunning: true };

    /// <summary>Live and monitored: the top bar's Pause stops the charts moving, while the session keeps collecting.</summary>
    public bool CanPause => _range.IsLive && IsMonitoring;

    // Resumed: what arrived meanwhile is plotted.
    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsPaused))
            return;
        if (!IsPaused && _range.IsLive)
            ShowNewSamples();
        ReportContextChanged();
    }

    /// <summary>"■  Stop monitoring" while monitored, "▶  Start monitoring" otherwise.</summary>
    [ObservableProperty] private string _monitorText = "▶  Start monitoring";
    [ObservableProperty] private string _monitorName = "Start monitoring";
    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private string? _monitorToolTip;

    /// <summary>Not monitored (#115): the banner says nothing is collected, and why.</summary>
    [ObservableProperty] private bool _showsMonitoringOff;
    [ObservableProperty] private string _monitoringOffReason = string.Empty;
    [ObservableProperty] private string? _bannerToolTip;

    /// <summary>"12s", "1:05" until the next sample; "--" while not monitored.</summary>
    [ObservableProperty] private string _nextSampleIn = "--";

    // Why nothing is collected, for the banner and the health score while not monitored.
    private string NotMonitoredText =>
        _connectionId is { } id && _monitoring is not null ? _monitoring.NotMonitoredReason(id) : MonitoringPlan.AutomaticOffText;

    private void ShowMonitoring()
    {
        var monitoring = IsMonitoring;
        var canStart   = monitoring || _connectionId is not { } id || _monitoring is null || _monitoring.CanStart(id);

        MonitorText    = monitoring ? "■  Stop monitoring" : "▶  Start monitoring";
        MonitorName    = monitoring ? "Stop monitoring" : "Start monitoring";
        CanStart       = canStart;
        MonitorToolTip = monitoring
            ? "Stop collecting live metrics, history and alerts for this connection until SqlVitals exits. Its active alerts end."
            : canStart
                ? "Collect live metrics, history and alerts for this connection until SqlVitals exits. The first sample is taken straight away."
                : NotMonitoredText;

        ShowsMonitoringOff  = !monitoring;
        MonitoringOffReason = _connectionId is { } active && _monitoring?.Decision(active) is { Reason: not MonitoringReason.AutomaticOff }
            ? NotMonitoredText
            : "Nothing is sent to the server for Live Metrics, history or alerts until you press Start.";
        BannerToolTip = canStart ? MonitorToolTip : NotMonitoredText;
        ShowHealthScore();
    }

    private void ShowCountdown() => NextSampleIn = IsMonitoring ? CountdownText(_session!.TimeUntilNextSample) : "--";

    [RelayCommand]
    private void ToggleMonitoring()
    {
        if (IsMonitoring)
            StopMonitoring();
        else
            StartMonitoring();
    }

    [RelayCommand]
    private void StartMonitoring()
    {
        // The saved connection's session is the manager's, so the dot, alerts and history follow it.
        if (_connectionId is { } id && _starter is not null)
            _ = _starter.StartAsync(id, ActivitySources.LiveMetrics);
        else
            _adHoc?.Start();
        OnSessionStateChanged(_session);
    }

    private void StopMonitoring()
    {
        if (_connectionId is { } id && _monitoring is not null)
            _monitoring.Stop(id, ActivitySources.LiveMetrics);
        else
            _adHoc?.Pause();
        OnSessionStateChanged(_session);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenMonitoringSettings() => ReportErrorsOf(_navigator.GoToAsync("Settings"));

    // ── How often it is sampled ───────────────────────────────────────────

    /// <summary>"every 10s", in the top bar beside the Live badge (#140).</summary>
    public IReadOnlyList<string> IntervalNames { get; }

    private static string IntervalLabel(int s) => s < 60 ? $"every {s}s" : $"every {s / 60}m {s % 60:00}s";

    /// <summary>This connection's sampling interval; it applies in the background too, and is kept while it isn't monitored.</summary>
    [ObservableProperty]
    private int _intervalIndex;

    partial void OnIntervalIndexChanged(int value)
    {
        if (value < 0)
            return;
        var seconds = Intervals[value];
        if (_connectionId is { } id && _monitoring is not null)
            _monitoring.SetInterval(id, seconds);
        else
            _adHoc?.IntervalSeconds = seconds;
        ShowCountdown();
    }

    // ── The health score (#36) ────────────────────────────────────────────
    // Always the latest sample's, also while a past range is on the charts. None while not
    // monitored: an old score would read as the server's health now.

    [ObservableProperty] private string _healthScoreText = "—";

    /// <summary>The score's level, which its colour follows; null (grey) while there is none.</summary>
    [ObservableProperty] private HealthLevel? _healthLevel;

    [ObservableProperty] private string? _healthToolTip;

    /// <summary>The score's breakdown: whose, the score, what to say without one, and whether paused.</summary>
    public HomeViewModel.ScoreBreakdown Breakdown => IsMonitoring
        ? new(_connectionName, _session!.Score, _session.HealthText, Paused: false)
        : new(_connectionName, null, NotMonitoredText, Paused: true);

    /// <summary>The score was pressed: the view opens its breakdown beside it.</summary>
    public event Action? BreakdownRequested;

    [RelayCommand]
    private void ShowBreakdown() => BreakdownRequested?.Invoke();

    private void ShowHealthScore()
    {
        var score = IsMonitoring ? _session!.Score : null;
        HealthScoreText = score?.Value.ToString(CultureInfo.CurrentCulture) ?? "—";
        HealthLevel     = score?.Level;
        HealthToolTip   = !IsMonitoring ? NotMonitoredText
                        : score is null ? _session!.HealthText
                        : $"{score.Summary()}\nClick to see which checks lowered it.";
        OnPropertyChanged(nameof(Breakdown));
        OnPropertyChanged(nameof(IsMonitoring));
        OnPropertyChanged(nameof(CanPause));
    }

    // ── The charts shown, and the process map ─────────────────────────────

    [ObservableProperty] private bool _showsWaits = true;
    [ObservableProperty] private bool _showsCpu;
    [ObservableProperty] private bool _showsThroughput;
    [ObservableProperty] private bool _showsPhysicalIo;
    [ObservableProperty] private bool _showsMemory;
    [ObservableProperty] private bool _showsProcessMap = true;

    /// <summary>Whether a chart is shown: its chip, and its card's ✕.</summary>
    public bool Shows(Chart chart) => chart switch
    {
        Chart.Waits      => ShowsWaits,
        Chart.Cpu        => ShowsCpu,
        Chart.Throughput => ShowsThroughput,
        Chart.PhysicalIo => ShowsPhysicalIo,
        Chart.Memory     => ShowsMemory,
        _                => ShowsProcessMap,
    };

    /// <summary>A chart's ✕: its chip goes off.</summary>
    [RelayCommand]
    private void Close(Chart chart)
    {
        switch (chart)
        {
            case Chart.Waits:      ShowsWaits      = false; break;
            case Chart.Cpu:        ShowsCpu        = false; break;
            case Chart.Throughput: ShowsThroughput = false; break;
            case Chart.PhysicalIo: ShowsPhysicalIo = false; break;
            case Chart.Memory:     ShowsMemory     = false; break;
            default:               ShowsProcessMap = false; break;
        }
    }

    /// <summary>The sessions the process map draws, read with each sample while it is shown.</summary>
    [ObservableProperty]
    private IReadOnlyList<ProcessNode>? _processes;

    /// <summary>The latest read of the process map; for tests.</summary>
    internal Task MapRead { get; private set; } = Task.CompletedTask;

    // The process map is only needed while it is on screen, so it is read by the page rather than
    // collected in the background.
    private async Task RefreshProcessMapAsync()
    {
        if (!_shown || _refreshingMap || !ShowsProcessMap)
            return;

        _refreshingMap = true;
        try
        {
            Processes = (await _repo.GetProcessesAsync()).ToList();
            ReportUpdated();
        }
        catch (OperationCanceledException)
        {
            // Left: nothing to say.
        }
        catch (Exception ex)
        {
            ReportSideError("Live Metrics process map", ex);
        }
        finally
        {
            _refreshingMap = false;
        }
    }
}
