using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// One server's Overview (#143, #189), the first page of its workspace: is it OK, and if not, why?
/// Its active alerts with the evidence for each, the health score with what is costing it points,
/// the headline numbers, what happened since the Overview was last opened, and the charts over the
/// top bar's time window with each alert's start marked. While the server can't be reached: why,
/// what to check, and what happened (#130). Only "Blocking right now" and "Since you last looked"
/// query the server, and only while it is monitored. What it shows is said by level and status;
/// the page colours it.
/// </summary>
public sealed partial class OverviewViewModel : PageViewModel
{
    // The live charts show at least this much, so a short interval doesn't make them a few seconds wide.
    private static readonly TimeSpan MinLiveSpan = TimeSpan.FromMinutes(10);

    // An alert change is queued to the history writer; reading straight away could miss it.
    internal static readonly TimeSpan ReloadAlertsDelay = TimeSpan.FromSeconds(1);

    // A collector's new sample is in its history already: read it straight away, as a timed read.
    private static readonly TimeSpan ReadElsewhereDelay = TimeSpan.FromMilliseconds(50);

    private readonly Guid                      _id;
    private readonly MonitoringManager         _monitoring;
    private readonly ConnectionSettingsService _settings;
    private readonly IWatchStarter             _starter;
    private readonly IUiDispatcher             _ui;
    private readonly IClipboard                _clipboard;
    private readonly Func<string, Task<string?>> _testConnection;
    private readonly TimeProvider              _time;

    private ConnectionStore     _store;
    private ConnectionSettings? _conn;
    private HistoryRange        _range = HistoryRange.Live;

    // From the history, and the scores taken since the page opened.
    private IReadOnlyList<ScorePoint>    _windowScores = [];
    private IReadOnlyList<ScorePoint>    _todayScores  = [];
    private readonly List<ScorePoint>    _live = new();
    private IReadOnlyList<Alert>         _alerts   = [];
    private IReadOnlyList<ActivityEntry> _activity = [];
    private ScoreHistoryPoint?           _lastReachable;
    private Coverage?                    _coverage;          // how much of the last day was monitored (#170)
    private HistoryLoad<MetricHistoryPoint>? _metrics;       // a past range's metrics; null while live
    private string?                      _historyProblem;

    // "Since you last looked": a row each for deadlocks, failed jobs and regressed queries.
    private SinceRow? _deadlocks, _failedJobs, _regressions;

    private DateTime? _lastScoreTime;
    private bool      _blockingLoading;
    private int       _loadVersion;
    private int       _sinceVersion;
    private bool      _shown;

    // Whether the server was read for "Since you last looked", and whether it was monitored then.
    private bool _sinceRead;
    private bool _sinceMonitored;

    // A collector watching this server instead of the app (#172), and what its history said last.
    private DateTime? _elsewhereSample;

    /// <param name="look">What "Since you last looked" covers: from when the Overview was opened before to now.</param>
    /// <param name="starter">What Start does (#172): asks whether the collector service should watch it.</param>
    /// <param name="ui">Settings saved elsewhere are told of on another thread: the page changes on this one.</param>
    /// <param name="testConnection">Test connection: opens one with the saved details and says what failed; null when it worked.</param>
    public OverviewViewModel(Guid connectionId, MonitoringManager monitoring, ConnectionSettingsService settings, LookWindow look,
                             IWatchStarter starter, IUiDispatcher ui, IClipboard clipboard,
                             Func<string, Task<string?>>? testConnection = null, TimeProvider? time = null)
    {
        _id             = connectionId;
        _monitoring     = monitoring;
        _settings       = settings;
        _starter        = starter;
        _ui             = ui;
        _clipboard      = clipboard;
        _testConnection = testConnection ?? ConnectionTest.TryOpenAsync;
        _time           = time ?? TimeProvider.System;
        Look            = look;

        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        TakeLiveScore();
        Render();

        // The "next try in" countdown while the server can't be reached (#169).
        WhileShownEvery(TimeSpan.FromSeconds(1), RenderOutageCountdown);
    }

    public Guid ConnectionId => _id;

    /// <summary>What "Since you last looked" covers.</summary>
    public LookWindow Look { get; }

    private DateTime NowUtc => _time.GetUtcNow().UtcDateTime;
    private DateTime TodayUtc => _time.GetLocalNow().Date.ToUniversalTime();

    // ── What the top bar shows (#140) ─────────────────────────────────────

    /// <summary>Live while the charts follow the samples; a past range picked otherwise.</summary>
    public bool IsLive => _range.IsLive;

    /// <summary>The server's monitoring interval while it is monitored; none while it isn't.</summary>
    public TimeSpan? LiveInterval => _range.IsLive && _monitoring.Get(_id) is { IsRunning: true }
        ? TimeSpan.FromSeconds(_monitoring.IntervalFor(_id))
        : null;

    /// <summary>Not monitored: nothing moves.</summary>
    public bool IsStopped => _monitoring.Get(_id) is not { IsRunning: true };

    public DateTime? DataTo => _range.IsLive ? null : PageBar.DataTo(_metrics, p => p.TimeUtc);

    // ── Where its links go: the window opens them (#141, #145) ────────────

    /// <summary>A page about this server: Deadlocks, Live charts, the page with the rest of "Since you last looked"…</summary>
    public event Action<Guid, string>? OpenPageRequested;

    /// <summary>An alert's or a check's evidence (#145): its page, filtered to the problem and set to its time.</summary>
    public event Action<Route>? EvidenceRequested;

    /// <summary>Make active: every server page then queries this connection.</summary>
    public event Action<Guid>? MakeActiveRequested;

    /// <summary>Edit connection: open it in Settings.</summary>
    public event Action<Guid>? EditRequested;

    /// <summary>Change, under the checks: its Health thresholds tab in Settings (#131).</summary>
    public event Action<Guid>? ThresholdsRequested;

    /// <summary>All alerts: the Alerts page, filtered to this connection.</summary>
    public event Action<Guid>? AlertsRequested;

    [RelayCommand] private void Edit() => EditRequested?.Invoke(_id);
    [RelayCommand] private void ChangeThresholds() => ThresholdsRequested?.Invoke(_id);
    [RelayCommand] private void MakeActive() => MakeActiveRequested?.Invoke(_id);
    [RelayCommand] private void AllAlerts() => AlertsRequested?.Invoke(_id);
    [RelayCommand] private void LiveCharts() => OpenPageRequested?.Invoke(_id, "LiveMetrics");
    [RelayCommand] private void BlockingChain() => EvidenceRequested?.Invoke(EvidenceRoutes.ForCheck(HealthIndicator.Blocking, _id, detail: null));

    // An alert's evidence (#145): its page, filtered to the problem and set to the alert's time.
    [RelayCommand]
    private void Evidence(OverviewAlert? row)
    {
        if (row is not null)
            EvidenceRequested?.Invoke(EvidenceRoutes.ForAlert(row.Alert));
    }

    // A check costing points: its page, filtered as an alert's would be, as the server is now.
    [RelayCommand]
    private void CostLink(CostRow? cost)
    {
        if (cost is not null)
            EvidenceRequested?.Invoke(EvidenceRoutes.ForCheck(cost.Indicator, _id, cost.Detail));
    }

    [RelayCommand]
    private void SinceLink(SinceRow? row)
    {
        if (row is not null)
            OpenPageRequested?.Invoke(_id, row.PageTag);
    }

    // ── Reading ───────────────────────────────────────────────────────────

    /// <summary>The last read of deadlocks and failed jobs from the server, for tests.</summary>
    internal Task SinceLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>The last read of the blocking sessions, for tests.</summary>
    internal Task BlockingLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Opening the page, and Refresh (F5): reads the window's scores, metrics and alerts, regressed
    /// queries since the last look and, while the server can't be reached, what happened; then asks
    /// the server for its deadlocks and failed jobs. A timed read, after an alert changed, reads the
    /// history again but not what is read for "Since you last looked", which stays for the same look.
    /// </summary>
    protected override Task LoadAsync(PageRead read, CancellationToken cancel) => ReadHistoryAsync(sinceToo: read != PageRead.Timed, cancel);

    protected override Task LoadAsync(CancellationToken cancel) => ReadHistoryAsync(sinceToo: true, cancel);

    // The top bar says what failed; the Overview has no verdict.
    protected override void ShowLoadError(Exception exception) { }

    private async Task ReadHistoryAsync(bool sinceToo, CancellationToken cancel)
    {
        var version = ++_loadVersion;
        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        Render();
        if (_conn is null)
            return;

        var reader   = _monitoring.HistoryReader;
        var nowUtc   = NowUtc;
        var range    = _range;
        var (fromUtc, toUtc, bucket) = range.IsLive ? LiveWindow(nowUtc) : PastWindow(range, nowUtc);
        var todayUtc = TodayUtc;
        var failing  = _monitoring.Get(_id)?.FailingSince?.ToUniversalTime();
        var look     = Look;
        var criteria = new RegressionCriteria(_store.RegressionThresholdPct, _store.RegressionMinExecutions);
        var elsewhere = Elsewhere is not null;
        var id       = _id;

        // A minute ahead, so a score or alert from a sample just taken is in range.
        var ahead = nowUtc.AddMinutes(1);
        try
        {
            var data = await Task.Run(() => (
                Window:    reader.ReadScores(id, fromUtc, (range.IsLive ? ahead : toUtc), bucket).Select(ScorePoint.From).ToList(),
                Today:     reader.ReadScores(id, todayUtc, ahead, TimeSpan.FromMinutes(1)).Select(ScorePoint.From).ToList(),
                Alerts:    reader.ReadAlerts(id, fromUtc < todayUtc ? fromUtc : todayUtc, ahead).Select(a => a.Alert).ToList(),
                // A collector's server has no session: its live window is its history too (#172).
                Metrics:   range.IsLive && !elsewhere ? null : new HistoryLoad<MetricHistoryPoint>(range, fromUtc, toUtc, bucket,
                                                                                     reader.ReadMetrics(id, fromUtc, toUtc, bucket)),
                Activity:  failing is { } f ? reader.ReadActivity(f.AddHours(-2), ahead, id).Select(r => r.Entry).ToList() : [],
                Reachable: failing is { } since ? reader.ReadLastReachableScore(id, since) : null,
                Coverage:  Coverage.Of(reader.ReadScores(id, nowUtc - HomeViewModel.CoverageSpan, ahead, HomeViewModel.CoverageBucket).Select(p => p.TimeUtc),
                                       nowUtc - HomeViewModel.CoverageSpan, nowUtc, HomeViewModel.CoverageBucket),
                Regressed: sinceToo ? QueryRegressionDetector.Detect(QueryRegressionDetector.FromHistory(
                               reader.ReadQueryTotals(id, SinceLastLooked.Baseline(look).FromUtc, SinceLastLooked.Baseline(look).ToUtc),
                               reader.ReadQueryTotals(id, look.FromUtc, look.ToUtc)), criteria).Count : (int?)null), cancel);
            if (version != _loadVersion)
                return;

            (_windowScores, _todayScores, _alerts, _metrics, _activity, _lastReachable, _coverage) =
                (data.Window, data.Today, data.Alerts, data.Metrics, data.Activity, data.Reachable, data.Coverage);
            if (data.Regressed is { } regressed)
                _regressions = RegressionsRow(regressed, criteria);
            _historyProblem = null;
        }
        catch (HistorySchemaTooNewException)
        {
            _historyProblem = "The history file is from a newer SqlVitals, so only what was collected since this page opened is shown.";
            _regressions    = new SinceRow("—", null, "Regressed queries", "Not checked: " + _historyProblem, "Query Regressions →", "QueryRegressions");
        }

        cancel.ThrowIfCancellationRequested();
        if (version != _loadVersion)
            return;
        Render();
        ReportContextChanged();
        if (sinceToo)
            SinceLoaded = RefreshSinceAsync();
        await RefreshBlockingAsync();
    }

    // The live window: the samples the session keeps, and at least ten minutes.
    private (DateTime From, DateTime To, TimeSpan Bucket) LiveWindow(DateTime nowUtc)
    {
        var interval = TimeSpan.FromSeconds(_monitoring.IntervalFor(_id));
        var span     = interval * MonitoringSession.MaxPoints;
        return (nowUtc - (span > MinLiveSpan ? span : MinLiveSpan), nowUtc, interval);
    }

    private static (DateTime From, DateTime To, TimeSpan Bucket) PastWindow(HistoryRange range, DateTime nowUtc)
    {
        var (fromUtc, toUtc) = range.Resolve(nowUtc);
        return (fromUtc, toUtc, HistoryRange.BucketFor(toUtc - fromUtc));
    }

    // The session's newest score, once, for the chart and the "was … at" line.
    private bool TakeLiveScore()
    {
        if (_monitoring.Get(_id) is not { IsRunning: true, Score: { } score } || score.Time == _lastScoreTime)
            return false;

        _lastScoreTime = score.Time;
        _live.Add(new ScorePoint(score.Time.ToUniversalTime(), score.Value, score.Level != HealthLevel.Unavailable));
        return true;
    }

    /// <summary>
    /// A past range, or Live again, from the top bar: the history again, but not what is read for
    /// "Since you last looked". A failure goes to the top bar, as a timed read's would.
    /// </summary>
    public Task PickRangeAsync(HistoryRange range)
    {
        _range   = range;
        _metrics = null;
        OnPropertyChanged(nameof(IsLive));
        ReportContextChanged();
        return PickedAsync(range);
    }

    private async Task PickedAsync(HistoryRange range)
    {
        try
        {
            await ReadHistoryAsync(sinceToo: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_range == range)
                ReportSideError("Overview history", ex);
        }
    }

    // ── While shown: the monitoring's news ────────────────────────────────

    protected override void OnShowing()
    {
        _shown = true;
        _monitoring.SessionStateChanged += OnSessionStateChanged;
        _monitoring.MonitoringChanged   += OnMonitoringChanged;
        _monitoring.AlertsChanged       += OnAlertsChanged;
        _settings.Changed               += OnSettingsChanged;
    }

    protected override void OnLeaving()
    {
        _shown = false;
        _monitoring.SessionStateChanged -= OnSessionStateChanged;
        _monitoring.MonitoringChanged   -= OnMonitoringChanged;
        _monitoring.AlertsChanged       -= OnAlertsChanged;
        _settings.Changed               -= OnSettingsChanged;
    }

    private ElsewhereWatch? Elsewhere => _monitoring.Get(_id) is { IsRunning: true } ? null : _monitoring.WatchFor(_id);

    private void OnSessionStateChanged(Guid connectionId)
    {
        if (connectionId != _id)
            return;

        // A collector watches it (#172): its news is in its history, read again when it has some.
        if (Elsewhere is { } watch)
        {
            if (watch.Last?.TimeUtc != _elsewhereSample)
            {
                _elsewhereSample = watch.Last?.TimeUtc;
                RefreshSoon(ReadElsewhereDelay);
            }
            else
            {
                Render();
            }
            return;
        }

        var newSample = TakeLiveScore();
        Render();
        ReportContextChanged();
        if (newSample)
        {
            ReportUpdated();
            BlockingLoaded = RefreshBlockingAsync();
            // The first sample since the page opened or the server came back: now it can be asked.
            if (!_sinceRead && _monitoring.Get(_id) is { FailingSince: null })
                SinceLoaded = RefreshSinceAsync();
        }
    }

    // Started or stopped, this server or another: what can be read from this one changes with its own.
    private void OnMonitoringChanged()
    {
        Render();
        ReportContextChanged();
        if (_monitoring.Get(_id) is { IsRunning: true } != _sinceMonitored)
            SinceLoaded = RefreshSinceAsync();
    }

    private void OnAlertsChanged(Guid connectionId, IReadOnlyList<AlertChange> changes)
    {
        if (connectionId == _id)
            RefreshSoon(ReloadAlertsDelay);
    }

    // Renamed, removed, made active, or its thresholds changed; told after the save was applied.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => _ui.Post(() =>
    {
        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        Render();
    });

    // ── The server: name, status, and what can be done to it ──────────────

    /// <summary>The connection's name; "Removed connection" once it is deleted.</summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>The connection was removed: the page says so and shows nothing else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsServer), nameof(ShowsActive), nameof(ShowsMakeActive))]
    private bool _isMissing;

    /// <summary>The server is there to show: its status, its buttons and the cards.</summary>
    public bool ShowsServer => !IsMissing;

    /// <summary>The server's state, as its Home tile has it: the dot, the chip, the score's colour.</summary>
    [ObservableProperty] private TileStatus _serverStatus = TileStatus.Off;

    [ObservableProperty] private string _statusLabel = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsActive), nameof(ShowsMakeActive))]
    private bool _isActive;

    /// <summary>The ACTIVE chip, on the active connection; Make active on any other.</summary>
    public bool ShowsActive => ShowsServer && IsActive;

    public bool ShowsMakeActive => ShowsServer && !IsActive;

    /// <summary>"prod-sql01 / Sales · Windows · monitoring · latest sample 21:52:10".</summary>
    [ObservableProperty] private string _connectionLine = string.Empty;

    [ObservableProperty] private string _monitorText = "Start monitoring";
    [ObservableProperty] private bool _canMonitor;
    [ObservableProperty] private string? _monitorToolTip;

    /// <summary>The score and what costs it, or (while it can't be reached) the outage.</summary>
    [ObservableProperty] private bool _showsHealth = true;
    [ObservableProperty] private bool _showsOutage;

    // Stop, or Start: in SqlVitals or by the collector, as the user chooses (#172).
    [RelayCommand]
    private Task Monitor()
    {
        if (_monitoring.IsMonitoring(_id))
        {
            _monitoring.Stop(_id, ActivitySources.Overview);
            return Task.CompletedTask;
        }
        return ReportErrorsOf(_starter.StartAsync(_id, ActivitySources.Overview));
    }

    private void Render()
    {
        if (_conn is not { } conn)
        {
            Name           = "Removed connection";
            IsMissing      = true;
            ServerStatus   = TileStatus.Off;
            ConnectionLine = string.Empty;
            return;
        }

        var session   = _monitoring.Get(_id);
        var tile      = ServerTile.From(HomeViewModel.InputFor(conn, _store, _monitoring), _time.GetLocalNow().DateTime);
        var running   = session is { IsRunning: true };
        var score     = running ? session!.Score : null;
        var elsewhere = Elsewhere;
        var watched   = elsewhere is { NotWatching: null };

        IsMissing      = false;
        Name           = conn.DisplayName;
        ServerStatus   = tile.Status;
        StatusLabel    = tile.StatusLabel;
        IsActive       = tile.IsActive;
        ConnectionLine = $"{conn.Server.Trim()} / {conn.DatabaseLabel} · {conn.AuthenticationLabel} · " +
                         (watched ? $"watched by {elsewhere!.By}"
                          : !running ? "not monitored"
                          : tile.IsActive ? "monitoring"
                          : "monitoring in the background") +
                         (score is { Level: not HealthLevel.Unavailable } ? $" · latest sample {score.Time:HH:mm:ss}" : string.Empty);

        MonitorText    = running ? "Pause monitoring" : watched ? "Watched by the collector" : "Start monitoring";
        CanMonitor     = !watched && (running || _monitoring.CanStart(_id));
        MonitorToolTip = watched ? _monitoring.WatchedElsewhereReason(_id)
            : running
            ? "Stop monitoring this connection until SqlVitals exits. Its active alerts end."
            : _monitoring.CanStart(_id)
                ? "Start monitoring this connection until SqlVitals exits, without switching to it."
                : $"Can't start: {_monitoring.NotMonitoredReason(_id)}";

        // A collector's server has no session: its outage is the tile's reason, on the health row.
        var unreachable = tile.Status == TileStatus.Unreachable && session is not null;
        ShowsHealth = !unreachable;
        ShowsOutage = unreachable;

        RenderAlerts(running || watched, unreachable);
        if (unreachable)
            RenderOutage(conn, session!, tile);
        else
        {
            RenderScore(conn, tile, score, elsewhere);
            RenderHeadlines(conn, session, watched);
            RenderSince();
        }
        RenderCharts();
        OnPropertyChanged(nameof(LiveInterval));
        OnPropertyChanged(nameof(IsStopped));
    }

    // ── Active alerts ─────────────────────────────────────────────────────

    /// <summary>An active alert: what, since when, in plain words, and Investigate.</summary>
    public sealed record OverviewAlert(HealthLevel Severity, string Title, string When, string Happening, string EvidenceText,
                                       string EvidenceToolTip, Alert Alert)
    {
        public HealthIndicator Indicator => Alert.Indicator;
    }

    [ObservableProperty] private IReadOnlyList<OverviewAlert> _alertsShown = [];

    /// <summary>The worst active alert's severity, which the card's border takes; null with none.</summary>
    [ObservableProperty] private HealthLevel? _worstSeverity;

    [ObservableProperty] private string _alertsTitle = "Active alerts";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoAlerts))]
    private string _noAlertsText = string.Empty;

    public bool ShowsNoAlerts => AlertsShown.Count == 0;

    partial void OnAlertsShownChanged(IReadOnlyList<OverviewAlert> value) => OnPropertyChanged(nameof(ShowsNoAlerts));

    private void RenderAlerts(bool running, bool unreachable)
    {
        var nowUtc   = NowUtc;
        var todayUtc = TodayUtc;

        // The session's are the active ones: one may not be saved yet, and one the history still has
        // open may have ended a moment ago. Stopping monitoring ends them all.
        var byId = _alerts.ToDictionary(a => a.Id);
        foreach (var live in _monitoring.Get(_id)?.ActiveAlerts ?? [])
            byId[live.Id] = live;

        // A collector's are in its history: the ones it has open (#172).
        var active = (!running ? [] : _monitoring.Get(_id) is { IsRunning: true } session ? session.ActiveAlerts
                      : Elsewhere?.ActiveAlerts ?? [])
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.StartedUtc)
            .Select(a =>
            {
                var evidence = EvidenceRoutes.For(a.Indicator);
                return new OverviewAlert(
                    a.Severity,
                    $"{HealthThresholds.Info(a.Indicator).Name} · {AlertText.Severity(a.Severity)}",
                    $"· since {Clock(a.StartedUtc)} · {AlertText.Duration(a.Duration(nowUtc))} so far",
                    AlertText.Happening(a),
                    "Investigate →",
                    $"Open {evidence.Text} for this server, filtered to this alert",
                    a);
            })
            .ToList();
        var endedToday = byId.Values.Count(a => a.EndedUtc >= todayUtc);

        AlertsShown   = Changed(AlertsShown, active);
        WorstSeverity = active.Count == 0 ? null : active.Max(a => a.Severity);
        AlertsTitle   = active.Count switch
        {
            0     => "Active alerts",
            1     => "1 active alert",
            var n => $"{n} active alerts",
        };
        NoAlertsText = _historyProblem
            ?? (!running ? "None: it isn't monitored, so no check is running."
                : unreachable ? "None: the server can't be reached, so no check can run."
                : "None: no check has stayed past its threshold long enough to alert.") +
               (endedToday switch { 0 => "", 1 => " 1 alert ended earlier today.", var n => $" {n} alerts ended earlier today." });
    }

    // A list replaced only when it changed, so redrawing on every sample doesn't take the keyboard
    // focus off a button in it (#140 learned this of the breadcrumb).
    private static IReadOnlyList<T> Changed<T>(IReadOnlyList<T> shown, IReadOnlyList<T> rows) =>
        shown.SequenceEqual(rows) ? shown : rows;

    // ── The score, and what costs it points ───────────────────────────────

    /// <summary>A check costing points: its name, "−20", why, and the page that explains it.</summary>
    /// <param name="Detail">What the check said, e.g. "Log 93% full (Sales)": its evidence's filter.</param>
    public sealed record CostRow(HealthLevel Level, string Name, string Points, string Why, string LinkText, string LinkToolTip,
                                 HealthIndicator Indicator, string? Detail = null);

    [ObservableProperty] private string _scoreText = "—";

    /// <summary>"Critical: blocking for 4 min", as its Home tile leads with (#173).</summary>
    [ObservableProperty] private string _scoreHeadline = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsContext))]
    private string _contextText = string.Empty;

    public bool ShowsContext => ContextText.Length > 0;

    /// <summary>How much of the last day was monitored (#170), and its longest gap.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCoverage))]
    private string _coverageText = string.Empty;

    [ObservableProperty] private string? _coverageToolTip;

    public bool ShowsCoverage => CoverageText.Length > 0;

    [ObservableProperty] private IReadOnlyList<CostRow> _costs = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoCosts))]
    private string _noCostsText = string.Empty;

    public bool ShowsNoCosts => Costs.Count == 0;

    partial void OnCostsChanged(IReadOnlyList<CostRow> value) => OnPropertyChanged(nameof(ShowsNoCosts));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPassing))]
    private IReadOnlyList<string> _passing = [];

    public bool ShowsPassing => Passing.Count > 0;

    /// <summary>Checks with no data: grey, no tick, left out of the score (#173).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsUnknown))]
    private IReadOnlyList<string> _unknown = [];

    public bool ShowsUnknown => Unknown.Count > 0;

    /// <summary>"Calm uses the default thresholds. "</summary>
    [ObservableProperty] private string _thresholdsText = string.Empty;

    private void RenderScore(ConnectionSettings conn, ServerTile tile, HealthScore? score, ElsewhereWatch? elsewhere)
    {
        ScoreText     = tile.ScoreText;
        ScoreHeadline = tile.Headline;
        var todayUtc  = TodayUtc;
        ContextText   = score is null
            ? tile.Footer
            : ScoreTrend.Context(ScoreTrend.Merge(_todayScores, _live.Where(p => p.TimeUtc >= todayUtc).ToList()), score.Value, Clock);

        // Whether a quiet day means a healthy one (#170): how much of it SqlVitals was watching.
        CoverageText    = _coverage?.Text() ?? string.Empty;
        CoverageToolTip = _coverage?.LongestGapText(Clock);

        var graded = score is { Level: not HealthLevel.Unavailable, Checks.Count: > 0 } ? score : null;
        var costs  = graded is null ? [] : ScoreCosts.Lowering(graded);
        Costs = Changed(Costs, costs.Select(c =>
        {
            var evidence = EvidenceRoutes.For(c.Indicator);
            var detail   = graded!.Checks.FirstOrDefault(check => check.Indicator == c.Indicator)?.Reading.Reason;
            return new CostRow(c.Level, c.Name, $"−{c.Points}", c.Why, $"{evidence.Text} →", $"Open {evidence.Text} for this server", c.Indicator, detail);
        }).ToList());
        NoCostsText = graded is null
            ? (elsewhere is not null
                  ? $"Not here: {elsewhere.By} keeps each sample's score, not the checks behind it. Its alerts are above."
                  : tile.Status == TileStatus.Off ? "Nothing to show: it isn't monitored." : "The checks show after the first sample.")
            : "Nothing: every check is within its thresholds.";

        Passing = Changed(Passing, graded is null ? [] : ScoreCosts.Passing(graded).Select(p => $"{p.Name} · {p.ValueText}").ToList());
        Unknown = Changed(Unknown, graded is null ? [] : ScoreCosts.Unknown(graded).Select(u => u.Name).ToList());

        ThresholdsText = conn.HealthThresholds is null
            ? $"{conn.DisplayName} uses the default thresholds. "
            : $"{conn.DisplayName} uses its own thresholds. ";

        var blocking = score?.Checks.FirstOrDefault(c => c.Indicator == HealthIndicator.Blocking);
        if (blocking is not { Level: HealthLevel.Warning or HealthLevel.Critical })
            ShowsBlocking = false;
    }

    // ── The headline numbers ──────────────────────────────────────────────

    /// <summary>A headline number with its sparkline, in its level's colour; null for one with no threshold.</summary>
    public sealed record HeadlineTile(string Label, string Value, HealthLevel? Level, IReadOnlyList<double> Trend, string Note, string ToolTip);

    [ObservableProperty] private IReadOnlyList<HeadlineTile> _headlines = [];

    // CPU, batch requests/s, waiting tasks and page life expectancy from the session's samples.
    private void RenderHeadlines(ConnectionSettings conn, MonitoringSession? session, bool watchedElsewhere)
    {
        // A collector's server: its history's last minutes (#172).
        var samples    = session is { IsRunning: true } ? Plotted(session)
                         : watchedElsewhere ? _metrics?.Items.Select(p => p.Averages).ToList() ?? []
                         : [];
        var thresholds = session?.Thresholds ?? _store.ThresholdsFor(conn);

        Headlines = Engine.Home.Headlines.From(samples, thresholds)
            .Select(h => new HeadlineTile(h.Label, h.ValueText, h.Level, h.Trend, h.Note, HeadlineToolTip(h.Kind, samples.Count)))
            .ToList();
    }

    private static string HeadlineToolTip(HeadlineKind kind, int samples) =>
        (kind switch
        {
            HeadlineKind.Cpu                => "SQL Server's share of the CPU, coloured by your CPU thresholds.",
            HeadlineKind.BatchRequests      => "Batches the server is running per second: how busy it is. No threshold, so it isn't coloured.",
            HeadlineKind.WaitingTasks       => "User tasks waiting on a lock, I/O, memory or anything else right now. No threshold, so it isn't coloured.",
            HeadlineKind.PageLifeExpectancy => "How long a page stays in the buffer pool, coloured by your page life expectancy thresholds.",
            _                               => string.Empty,
        }) + (samples == 0 ? "" : $" The line: the last {samples} samples.");

    // ── Since you last looked ─────────────────────────────────────────────

    /// <summary>One line of "Since you last looked": how many, of what, a detail, and the page with the rest.</summary>
    /// <param name="Level">The count's colour: green for none, else its level; null greys it (not read).</param>
    public sealed record SinceRow(string Count, HealthLevel? Level, string What, string Detail, string LinkText, string PageTag);

    [ObservableProperty] private string _sinceWhen = string.Empty;
    [ObservableProperty] private IReadOnlyList<SinceRow> _sinceRows = [];

    private void RenderSince()
    {
        var nowUtc = NowUtc;
        SinceWhen = Look switch
        {
            { IsFirstLook: true }                     => "In the last 24 hours: this is the first time this Overview has been opened.",
            { Capped: true, LastLookedUtc: { } last } => $"In the last 7 days. You last looked {When(last)}.",
            { LastLookedUtc: { } last }               => $"Since {When(last)}, {AlertText.Duration(nowUtc - last)} ago.",
            _                                         => string.Empty,
        };
        SinceRows = Changed(SinceRows, new[]
        {
            _deadlocks   ?? new SinceRow("…", null, "Deadlocks", "Reading…", "Deadlocks →", "Deadlocks"),
            _failedJobs  ?? new SinceRow("…", null, "Failed Agent jobs", "Reading…", "Agent Jobs →", "AgentJobs"),
            _regressions ?? new SinceRow("…", null, "Regressed queries", "Reading the monitoring history…", "Query Regressions →", "QueryRegressions"),
        });
    }

    // Deadlocks from system_health and failed jobs from msdb, read from the server while it is
    // monitored and answering. Each read fails on its own: one without permission leaves the other.
    private async Task RefreshSinceAsync()
    {
        var version = ++_sinceVersion;
        var current = _monitoring.Get(_id);
        _sinceMonitored = current is { IsRunning: true };
        _sinceRead      = false;
        if (current is { IsRunning: true, LastSuccess: null, FailingSince: null })
        {
            // Read once the first sample shows the server answers (see OnSessionStateChanged).
            _deadlocks  = new SinceRow("…", null, "Deadlocks", "Waiting for the first sample…", "Deadlocks →", "Deadlocks");
            _failedJobs = new SinceRow("…", null, "Failed Agent jobs", "Waiting for the first sample…", "Agent Jobs →", "AgentJobs");
            RenderSince();
            return;
        }
        if (current is not { IsRunning: true, FailingSince: null } session)
        {
            var why = current is { IsRunning: true } ? "it can't be reached" : "it isn't monitored";
            _deadlocks  = new SinceRow("—", null, "Deadlocks", $"Not checked: {why}. They are read from the server's system_health session.", "Deadlocks →", "Deadlocks");
            _failedJobs = new SinceRow("—", null, "Failed Agent jobs", $"Not checked: {why}. They are read from msdb.", "Agent Jobs →", "AgentJobs");
            RenderSince();
            return;
        }

        _sinceRead = true;

        _deadlocks  ??= new SinceRow("…", null, "Deadlocks", "Reading the server's system_health session…", "Deadlocks →", "Deadlocks");
        _failedJobs ??= new SinceRow("…", null, "Failed Agent jobs", "Reading msdb…", "Agent Jobs →", "AgentJobs");
        RenderSince();

        var repository = session.Repository;
        var look       = Look;
        var deadlocks  = Task.Run(() => repository.GetDeadlockHistoryAsync());
        var jobs       = Task.Run(() => repository.GetAgentJobsAsync());
        // Read even when a newer read makes this one's result unwanted.
        _ = jobs.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        try
        {
            var history = await deadlocks;
            if (version != _sinceVersion)
                return;
            var found = SinceLastLooked.Deadlocks(history.Deadlocks, look);
            _deadlocks = history.Problem is { } problem && history.Deadlocks.Count == 0
                ? new SinceRow("—", null, "Deadlocks", $"Couldn't read them: {problem}", "Deadlocks →", "Deadlocks")
                : new SinceRow(Count(found.Count), CountLevel(found.Count, HealthLevel.Critical), Plural(found.Count, "Deadlock", "Deadlocks"),
                               found.Count == 0 ? "None." : Latest(found[0]),
                               "Deadlocks →", "Deadlocks");
        }
        catch (Exception ex)
        {
            if (version != _sinceVersion)
                return;
            AppLog.Error("Overview: could not read the deadlocks", ex);
            _deadlocks = new SinceRow("—", null, "Deadlocks", $"Couldn't read them: {ErrorStatus.Format(ex)}", "Deadlocks →", "Deadlocks");
        }
        RenderSince();

        try
        {
            var list = await jobs;
            if (version != _sinceVersion)
                return;
            var offset = list.ServerNow is { } serverNow ? ServerClock.Offset(serverNow, NowUtc) : TimeZoneInfo.Local.GetUtcOffset(NowUtc);
            var failed = SinceLastLooked.FailedJobs(list.Jobs, look, offset);
            _failedJobs = !list.Available
                ? new SinceRow("—", null, "Failed Agent jobs", list.Problem ?? "Agent jobs can't be read here.", "Agent Jobs →", "AgentJobs")
                : new SinceRow(Count(failed.Count), CountLevel(failed.Count, HealthLevel.Critical), Plural(failed.Count, "Failed Agent job", "Failed Agent jobs"),
                               failed.Count == 0 ? "None." : Names(failed.Select(j => j.Name).ToList()),
                               "Agent Jobs →", "AgentJobs");
        }
        catch (Exception ex)
        {
            if (version != _sinceVersion)
                return;
            AppLog.Error("Overview: could not read the Agent jobs", ex);
            _failedJobs = new SinceRow("—", null, "Failed Agent jobs", $"Couldn't read them: {ErrorStatus.Format(ex)}", "Agent Jobs →", "AgentJobs");
        }
        RenderSince();
    }

    private static SinceRow RegressionsRow(int count, RegressionCriteria criteria) =>
        new(Count(count), CountLevel(count, HealthLevel.Warning), Plural(count, "Regressed query", "Regressed queries"),
            string.Format(CultureInfo.CurrentCulture,
                          "{0} more than {1:0.#} % slower than in the week before, from the monitoring history.",
                          count == 0 ? "None" : count == 1 ? "1 query" : $"{count} queries", criteria.ThresholdPct),
            "Query Regressions →", "QueryRegressions");

    // "Latest at 13:42:10: victim SPID 55 · app_login · dbo.usp_Transfer", or the objects fought over.
    private string Latest(DeadlockReport report)
    {
        var what = report.VictimText.Length > 0 ? $"victim {report.VictimText}" : report.ObjectsText;
        return what.Length == 0 ? $"Latest at {When(report.TimestampUtc)}." : $"Latest at {When(report.TimestampUtc)}: {what}";
    }

    // "Nightly ETL, Backup logs and 3 more".
    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(2))} and {names.Count - 2} more";

    private static string Count(int count) => count.ToString(CultureInfo.CurrentCulture);

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private static HealthLevel CountLevel(int count, HealthLevel level) => count == 0 ? HealthLevel.Healthy : level;

    // ── Blocking right now ────────────────────────────────────────────────

    [ObservableProperty] private bool _showsBlocking;
    [ObservableProperty] private bool _showsBlockingChain;
    [ObservableProperty] private string _blockingFor = string.Empty;
    [ObservableProperty] private string _blockingHead = string.Empty;
    [ObservableProperty] private string _blockingWaiting = string.Empty;
    [ObservableProperty] private string _blockingStatement = string.Empty;
    [ObservableProperty] private string _blockingNote = string.Empty;

    // Reads the sessions from the server, once per sample at most, while blocking is past its threshold.
    private async Task RefreshBlockingAsync()
    {
        var session  = _monitoring.Get(_id);
        var blocking = session is { IsRunning: true } ? session.Score?.Checks.FirstOrDefault(c => c.Indicator == HealthIndicator.Blocking) : null;
        var show     = blocking is { Level: HealthLevel.Warning or HealthLevel.Critical } && ShowsHealth;
        if (show && !ShowsBlocking)
        {
            // Nothing from an earlier spell of blocking.
            ShowsBlockingChain = false;
            BlockingFor        = string.Empty;
            BlockingNote       = "Reading the sessions…";
        }
        ShowsBlocking = show;
        if (!show || _blockingLoading || !_shown)
            return;

        _blockingLoading = true;
        try
        {
            var now = BlockingNow.From(await session!.Repository.GetProcessesAsync());
            ShowsBlockingChain = now is not null;
            if (now is null)
            {
                BlockingFor  = string.Empty;
                BlockingNote = "No blocking chain is visible now: it may have just cleared.";
                return;
            }

            BlockingFor       = AlertText.Duration(now.LongestWait).ToUpper(CultureInfo.CurrentCulture);
            BlockingHead      = $"Session {now.HeadSessionId} · {now.Who}";
            BlockingWaiting   = now.WaitingText;
            BlockingStatement = now.Statement ?? (now.HeadIsSleeping ? "None: the session is idle, holding a transaction open." : "None");
            BlockingNote      = now.OtherChains switch
            {
                0     => string.Empty,
                1     => "…and 1 more blocking chain.",
                var n => $"…and {n} more blocking chains.",
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("Overview: could not read the blocking sessions", ex);
            ShowsBlockingChain = false;
            BlockingNote       = $"Couldn't read the sessions: {ErrorStatus.Format(ex)}";
        }
        finally
        {
            _blockingLoading = false;
        }
    }

    // ── The charts ────────────────────────────────────────────────────────

    /// <summary>
    /// What the charts draw over the top bar's window: the scores, each sample's or bucket's
    /// metrics (UTC on this PC's clock), and a marker where each alert started. The page draws
    /// them in the theme's colours.
    /// </summary>
    public sealed record OverviewCharts(DateTime FromUtc, DateTime ToUtc, TimeSpan Bucket, IReadOnlyList<ScorePoint> Scores,
                                       IReadOnlyList<(DateTime TimeUtc, LiveMetricSample Sample)> Samples, IReadOnlyList<Marker> Markers)
    {
        public static readonly OverviewCharts None = new(default, default, TimeSpan.FromMinutes(1), [], [], []);
    }

    /// <summary>An alert's start on the charts: "21:52 CPU", in its severity's colour.</summary>
    public sealed record Marker(DateTime TimeUtc, string Label, HealthLevel Severity);

    [ObservableProperty] private OverviewCharts _charts = OverviewCharts.None;
    [ObservableProperty] private string _chartsTitle = "Charts";
    [ObservableProperty] private string _chartsNote = string.Empty;

    /// <summary>The score chart's name for a screen reader: "Health score: lowest 72, latest 90".</summary>
    [ObservableProperty] private string _scoreChartName = string.Empty;

    private void RenderCharts()
    {
        var nowUtc = NowUtc;
        var (fromUtc, toUtc, bucket) = _range.IsLive ? LiveWindow(nowUtc)
                                     : _metrics is { } load ? (load.FromUtc, load.ToUtc, load.Bucket)
                                     : PastWindow(_range, nowUtc);

        // Live: the session's samples, put on this PC's clock; a past range: the history's averages.
        var rows = _range.IsLive && Elsewhere is null ? LiveSamples() : _metrics?.Items.Select(p => (p.TimeUtc, p.Averages)).ToList() ?? [];

        var markers = _alerts
            .Concat(_monitoring.Get(_id)?.ActiveAlerts ?? [])
            .DistinctBy(a => a.Id)
            .Where(a => a.StartedUtc >= fromUtc && a.StartedUtc <= toUtc)
            .Select(a => new Marker(a.StartedUtc, $"{Clock(a.StartedUtc)} {ServerTile.ShortName(a.Indicator)}", a.Severity))
            .ToList();

        var scores = ScoreTrend.Merge(_windowScores, _live.Where(p => p.TimeUtc >= fromUtc).ToList());
        Charts = new OverviewCharts(fromUtc, toUtc, bucket, scores, rows, markers);

        var running = _monitoring.Get(_id) is { IsRunning: true };
        ChartsTitle = _range.IsLive ? "Live" : _range.Label;
        ChartsNote  = _historyProblem ?? (_range.IsLive
            ? (running
                ? $"The last {Span(toUtc - fromUtc)}, a sample every {ConnectionHistory.FormatBucket(bucket)}. "
                : "Not monitored: nothing new is collected. Pick a past range in the top bar to see the history. ")
            : rows.Count == 0
                ? "Nothing recorded in this range: metrics are recorded while SqlVitals monitors this connection. "
                : $"Averages per {ConnectionHistory.FormatBucket(bucket)} from the monitoring history. ")
            + (markers.Count > 0 ? "Dashed lines: alerts starting. " : string.Empty)
            + "Shaded red: the server couldn't be reached; grey: not monitored. Times on this PC's clock.";

        ScoreChartName = scores.Count == 0
            ? "Health score: no scores recorded in this window"
            : $"Health score: lowest {scores.Min(p => p.Score)}, latest {scores[^1].Score}";
    }

    // The session's samples, from the server's clock to UTC on this PC's (see ServerClock).
    private IReadOnlyList<(DateTime TimeUtc, LiveMetricSample Sample)> LiveSamples()
    {
        if (_monitoring.Get(_id) is not { IsRunning: true, Samples.Count: > 0, LastSuccess: { } last } session)
            return [];
        var offset = ServerClock.Offset(session.Samples[^1].Time, last.ToUniversalTime());
        return Plotted(session).Select(s => (DateTime.SpecifyKind(s.Time - offset, DateTimeKind.Utc), s)).ToList();
    }

    // The session's first sample has no rates (nothing to diff against), so it would draw a drop to
    // zero: left out once there are others, as the history leaves it out. Until the session has
    // kept its most samples, the first it holds is that one.
    private static IReadOnlyList<LiveMetricSample> Plotted(MonitoringSession session) =>
        session.Samples.Count is > 1 and < MonitoringSession.MaxPoints ? session.Samples.Skip(1).ToList() : session.Samples;

    // "10 minutes", "1 hour".
    private static string Span(TimeSpan span) =>
        span < TimeSpan.FromHours(1) ? $"{span.TotalMinutes:0} minutes"
        : span == TimeSpan.FromHours(1) ? "hour"
        : $"{span.TotalHours:0.#} hours";

    // ── The server can't be reached (#130) ────────────────────────────────

    /// <summary>A line of "What happened": its time, and what.</summary>
    public sealed record DetailLine(string When, string Text);

    // The last score taken while the server could be reached, and what lowered it if known.
    private sealed record LastGood(DateTime TimeUtc, int Score, string? LoweredBy);

    [ObservableProperty] private string _outageTitle = string.Empty;
    [ObservableProperty] private string _outageError = string.Empty;
    [ObservableProperty] private string _outageSince = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _thingsToCheck = [];
    [ObservableProperty] private string _beforeText = string.Empty;
    [ObservableProperty] private IReadOnlyList<DetailLine> _timeline = [];

    /// <summary>What Retry or Test connection found; null until one is pressed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOutageAction))]
    private string? _outageAction;

    public bool ShowsOutageAction => OutageAction is not null;

    private void RenderOutage(ConnectionSettings conn, MonitoringSession session, ServerTile tile)
    {
        var (host, _, _) = Engine.Home.ThingsToCheck.Parse(conn.Server);
        OutageTitle    = $"SqlVitals can't reach {host}";
        OutageError    = session.LastError ?? tile.ReasonToolTip ?? "The last collection failed.";
        ThingsToCheck  = Changed(ThingsToCheck, Engine.Home.ThingsToCheck.For(conn.Server, session.LastError));
        RenderOutageCountdown();

        // From this run when a sample worked in it; otherwise the history knows the last one.
        var lastGood = session.LastGoodScore is { } good
            ? new LastGood(good.Time.ToUniversalTime(), good.Value, good.Lowering.Count == 0 ? null : good.LoweredBy())
            : _lastReachable is { } stored ? new LastGood(stored.TimeUtc, stored.Score, null) : null;
        BeforeText = lastGood is null
            ? "No sample has worked since monitoring started."
            : $"Score {lastGood.Score} at {When(lastGood.TimeUtc)}" +
              (lastGood.LoweredBy is { } why ? $", lowered by {why}."
               : lastGood.Score == HealthScore.Max ? ": every check within its thresholds."
               : ".");

        var nowUtc          = NowUtc;
        var failingSinceUtc = (session.FailingSince ?? _time.GetLocalNow().DateTime).ToUniversalTime();
        Timeline = Changed(Timeline, OutageTimeline.Build(
                lastGood is null ? null : (lastGood.TimeUtc, lastGood.Score),
                failingSinceUtc,
                session.ConsecutiveFailures,
                session.IsRunning ? nowUtc + session.TimeUntilNextSample : null,
                _activity,
                _alerts.Concat(session.ActiveAlerts).DistinctBy(a => a.Id)
                       .Where(a => a.StartedUtc >= failingSinceUtc.AddHours(-2) || a.EndedUtc >= failingSinceUtc.AddHours(-2)),
                nowUtc)
            .Select(e => new DetailLine(Clock(e.TimeUtc), e.Text))
            .ToList());
    }

    // "Since 21:41:05 · 28 failed attempts · next try in 12 s", kept counting down.
    private void RenderOutageCountdown()
    {
        if (!ShowsOutage || _monitoring.Get(_id) is not { } session)
            return;

        var attempts = session.ConsecutiveFailures == 1 ? "1 failed attempt" : $"{session.ConsecutiveFailures} failed attempts";
        OutageSince = (session.FailingSince is { } since ? $"Since {When(since.ToUniversalTime())} · " : string.Empty) + attempts +
                      (session.IsRunning ? $" · next try in {AlertText.Duration(session.TimeUntilNextSample)}" : " · monitoring is stopped");
    }

    // Retry now: a sample at once. While it runs, the button can't be pressed again.
    [RelayCommand]
    private async Task Retry()
    {
        if (_monitoring.Get(_id) is not { } session)
            return;

        OutageAction = "Trying to reach it…";
        try
        {
            await session.CollectNowAsync();
            OutageAction = "Reached it: the sample worked.";
        }
        catch
        {
            OutageAction = $"Still can't reach it at {_time.GetLocalNow():HH:mm:ss}.";
        }
        finally
        {
            Render();
        }
    }

    // Test connection: opens one with the saved details, and closes it. What it throws goes to the top bar.
    [RelayCommand]
    private Task Test() => ReportErrorsOf(TestAsync());

    private async Task TestAsync()
    {
        if (_conn is not { } conn)
            return;
        if (conn.NeedsPassword)
        {
            OutageAction = "The password isn't saved. Enter it with Edit connection.";
            return;
        }

        OutageAction = "Testing the connection…";
        var error = await _testConnection(conn.ConnectionString);
        OutageAction = error is null
            ? "Connection successful: the server answers. The next sample should work; Retry now takes it straight away."
            : $"Connection failed: {error}";
    }

    [RelayCommand]
    private void CopyError() => _clipboard.SetText(OutageError);

    // ── Times ─────────────────────────────────────────────────────────────

    // "21:52" on this PC's clock.
    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    // "21:52:10" today, "30 Sep 21:52" on another day, on this PC's clock.
    private string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == _time.GetLocalNow().Date
            ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }
}
