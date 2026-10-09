using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// One server's Overview (#143), the first page of its workspace: is it OK, and if not, why? Its
/// active alerts with the evidence for each, the health score with what is costing it points, the
/// headline numbers, what happened since the Overview was last opened, and the charts over the top
/// bar's time window with each alert's start marked. While the server can't be reached: why, what
/// to check, and what happened (#130). Only "Blocking right now" and "Since you last looked" query
/// the server, and only while it is monitored.
/// </summary>
public partial class OverviewPage : Page, ICodeBehindPage, IPageContext
{
    // Its view model (#183): the page factory runs its lifecycle; the code-behind still reads.
    internal OverviewViewModel ViewModel { get; } = new();

    // Live while the charts follow the samples, History while a past range is picked (#140).
    public PageKind Kind => _range.IsLive ? PageKind.Live : PageKind.History;

    // The server's monitoring interval while it is monitored; no interval to show while it isn't.
    public TimeSpan? LiveInterval => _range.IsLive && _monitoring.Get(_id) is { IsRunning: true }
        ? TimeSpan.FromSeconds(_monitoring.IntervalFor(_id))
        : null;

    public bool IsPaused => _monitoring.Get(_id) is not { IsRunning: true };

    public DateTime? DataTo => _range.IsLive ? null : PageBar.DataTo(_metrics, p => p.TimeUtc);

    // The time window goes to the top bar, without a baseline: the Overview compares nothing.
    public FrameworkElement? TakeBarTools() => PageBar.Detach(RangePicker);

    // The live charts show at least this much, so a short interval doesn't make them a few seconds wide.
    private static readonly TimeSpan MinLiveSpan = TimeSpan.FromMinutes(10);

    private readonly Guid                      _id;
    private readonly MonitoringManager         _monitoring;
    private readonly ConnectionSettingsService _settings;

    /// <summary>What "Since you last looked" covers: from when the Overview was opened before to when it was opened now.</summary>
    public LookWindow Look { get; }

    // An alert change is queued to the history writer; reading straight away could miss it.
    private static readonly TimeSpan ReloadAlertsDelay = TimeSpan.FromSeconds(1);

    private ConnectionStore     _store;
    private ConnectionSettings? _conn;
    private HistoryRange        _range = HistoryRange.Live;

    // From the history, and the scores taken since the page opened.
    private IReadOnlyList<ScorePoint> _windowScores = [];
    private IReadOnlyList<ScorePoint> _todayScores  = [];
    private readonly List<ScorePoint> _live = new();
    private IReadOnlyList<Alert>         _alerts   = [];
    private IReadOnlyList<ActivityEntry> _activity = [];
    private ScoreHistoryPoint?           _lastReachable;
    private HistoryLoad<MetricHistoryPoint>? _metrics;   // a past range's metrics; null while live
    private string?                      _historyProblem;

    // "Since you last looked": a row each for deadlocks, failed jobs and regressed queries.
    private SinceRow? _deadlocks, _failedJobs, _regressions;

    private DateTime? _lastScoreTime;
    private bool      _blockingLoading;
    private int       _loadVersion;
    private int       _sinceVersion;

    // Whether the server was read for "Since you last looked", and whether it was monitored then.
    private bool _sinceRead;
    private bool _sinceMonitored;

    // The top bar: what this page read, what went wrong, and its kind or window (#166).
    private readonly IShellStatus _shell;

    public OverviewPage(Guid connectionId, MonitoringManager monitoring, ConnectionSettingsService settings, LookWindow look,
                        IShellStatus? shell = null)
    {
        _shell      = shell ?? CompositionRoot.Get<IShellStatus>();
        _id         = connectionId;
        _monitoring = monitoring;
        _settings   = settings;
        Look        = look;
        InitializeComponent();

        RangePicker.OffersBaseline = false;
        RangePicker.SetHistoryAvailable(true);
        BuildLegends();

        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        TakeLiveScore();
        Render();

        // The "next try in" countdown while the server can't be reached, on the view model's clock (#169).
        ViewModel.TickWhileShown(TimeSpan.FromSeconds(1), RenderOutageCountdown);
        SizeChanged += (_, e) => ChartsGrid.Columns = e.NewSize.Width < 1000 ? 2 : 3;
        Loaded += (_, _) =>
        {
            _monitoring.SessionStateChanged += OnSessionStateChanged;
            _monitoring.MonitoringChanged   += OnMonitoringChanged;
            _monitoring.AlertsChanged       += OnAlertsChanged;
            _settings.Changed               += OnSettingsChanged;
            // MainWindow loads the page before it's shown, when the sessions can't be read yet.
            _ = RefreshBlockingAsync();
        };
        Unloaded += (_, _) =>
        {
            _monitoring.SessionStateChanged -= OnSessionStateChanged;
            _monitoring.MonitoringChanged   -= OnMonitoringChanged;
            _monitoring.AlertsChanged       -= OnAlertsChanged;
            _settings.Changed               -= OnSettingsChanged;
        };
    }

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

    public Guid ConnectionId => _id;

    /// <summary>The last read of deadlocks and failed jobs from the server, for tests.</summary>
    internal Task SinceLoaded { get; private set; } = Task.CompletedTask;

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opening the page, and Refresh (F5): reads the window's scores, metrics and alerts, regressed
    /// queries since the last look and, while the server can't be reached, what happened; then asks
    /// the server for its deadlocks and failed jobs.
    /// </summary>
    public Task RefreshAsync() => LoadAsync(sinceToo: true);

    // An alert changing or another time window: the history again, but not what is read for
    // "Since you last looked", which stays as it was for the same look.
    private async Task LoadAsync(bool sinceToo)
    {
        var version = ++_loadVersion;
        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        Render();
        if (_conn is null)
            return;

        var reader   = _monitoring.HistoryReader;
        var nowUtc   = DateTime.UtcNow;
        var range    = _range;
        var (fromUtc, toUtc, bucket) = range.IsLive ? LiveWindow(nowUtc) : PastWindow(range, nowUtc);
        var todayUtc = DateTime.Today.ToUniversalTime();
        var failing  = _monitoring.Get(_id)?.FailingSince?.ToUniversalTime();
        var look     = Look;
        var criteria = new RegressionCriteria(_store.RegressionThresholdPct, _store.RegressionMinExecutions);

        // A minute ahead, so a score or alert from a sample just taken is in range.
        var ahead = nowUtc.AddMinutes(1);
        try
        {
            var data = await Task.Run(() => (
                Window:    reader.ReadScores(_id, fromUtc, (range.IsLive ? ahead : toUtc), bucket).Select(ScorePoint.From).ToList(),
                Today:     reader.ReadScores(_id, todayUtc, ahead, TimeSpan.FromMinutes(1)).Select(ScorePoint.From).ToList(),
                Alerts:    reader.ReadAlerts(_id, fromUtc < todayUtc ? fromUtc : todayUtc, ahead).Select(a => a.Alert).ToList(),
                Metrics:   range.IsLive ? null : new HistoryLoad<MetricHistoryPoint>(range, fromUtc, toUtc, bucket,
                                                                                     reader.ReadMetrics(_id, fromUtc, toUtc, bucket)),
                Activity:  failing is { } f ? reader.ReadActivity(f.AddHours(-2), ahead, _id).Select(r => r.Entry).ToList() : [],
                Reachable: failing is { } since ? reader.ReadLastReachableScore(_id, since) : null,
                Regressed: sinceToo ? QueryRegressionDetector.Detect(QueryRegressionDetector.FromHistory(
                               reader.ReadQueryTotals(_id, SinceLastLooked.Baseline(look).FromUtc, SinceLastLooked.Baseline(look).ToUtc),
                               reader.ReadQueryTotals(_id, look.FromUtc, look.ToUtc)), criteria).Count : (int?)null));
            if (version != _loadVersion)
                return;

            (_windowScores, _todayScores, _alerts, _metrics, _activity, _lastReachable) =
                (data.Window, data.Today, data.Alerts, data.Metrics, data.Activity, data.Reachable);
            if (data.Regressed is { } regressed)
                _regressions = RegressionsRow(regressed, criteria);
            _historyProblem = null;
        }
        catch (HistorySchemaTooNewException)
        {
            _historyProblem = "The history file is from a newer SqlVitals, so only what was collected since this page opened is shown.";
            _regressions    = new SinceRow("—", Muted, "Regressed queries", "Not checked: " + _historyProblem, "Query Regressions →", "QueryRegressions");
        }

        if (version != _loadVersion)
            return;
        Render();
        _shell.ReportContextChanged(this);
        if (sinceToo)
            SinceLoaded = RefreshSinceAsync();
        await RefreshBlockingAsync();
    }

    // The view model's timed read (#169): after an alert changed, without "Since you last looked".
    public Task ReadTimedAsync() => LoadAsync(sinceToo: false);

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

    // ── Since you last looked ─────────────────────────────────────────────────

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
            _deadlocks  = new SinceRow("…", Muted, "Deadlocks", "Waiting for the first sample…", "Deadlocks →", "Deadlocks");
            _failedJobs = new SinceRow("…", Muted, "Failed Agent jobs", "Waiting for the first sample…", "Agent Jobs →", "AgentJobs");
            RenderSince();
            return;
        }
        if (current is not { IsRunning: true, FailingSince: null } session)
        {
            var why = current is { IsRunning: true } ? "it can't be reached" : "it isn't monitored";
            _deadlocks  = new SinceRow("—", Muted, "Deadlocks", $"Not checked: {why}. They are read from the server's system_health session.", "Deadlocks →", "Deadlocks");
            _failedJobs = new SinceRow("—", Muted, "Failed Agent jobs", $"Not checked: {why}. They are read from msdb.", "Agent Jobs →", "AgentJobs");
            RenderSince();
            return;
        }

        _sinceRead = true;

        _deadlocks  ??= new SinceRow("…", Muted, "Deadlocks", "Reading the server's system_health session…", "Deadlocks →", "Deadlocks");
        _failedJobs ??= new SinceRow("…", Muted, "Failed Agent jobs", "Reading msdb…", "Agent Jobs →", "AgentJobs");
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
                ? new SinceRow("—", Muted, "Deadlocks", $"Couldn't read them: {problem}", "Deadlocks →", "Deadlocks")
                : new SinceRow(Count(found.Count), CountBrush(found.Count, HealthLevel.Critical), Plural(found.Count, "Deadlock", "Deadlocks"),
                               found.Count == 0 ? "None." : Latest(found[0]),
                               "Deadlocks →", "Deadlocks");
        }
        catch (Exception ex)
        {
            if (version != _sinceVersion)
                return;
            AppLog.Error("Overview: could not read the deadlocks", ex);
            _deadlocks = new SinceRow("—", Muted, "Deadlocks", $"Couldn't read them: {ErrorStatus.Format(ex)}", "Deadlocks →", "Deadlocks");
        }
        RenderSince();

        try
        {
            var list = await jobs;
            if (version != _sinceVersion)
                return;
            var offset = list.ServerNow is { } serverNow ? ServerClock.Offset(serverNow, DateTime.UtcNow) : TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            var failed = SinceLastLooked.FailedJobs(list.Jobs, look, offset);
            _failedJobs = !list.Available
                ? new SinceRow("—", Muted, "Failed Agent jobs", list.Problem ?? "Agent jobs can't be read here.", "Agent Jobs →", "AgentJobs")
                : new SinceRow(Count(failed.Count), CountBrush(failed.Count, HealthLevel.Critical), Plural(failed.Count, "Failed Agent job", "Failed Agent jobs"),
                               failed.Count == 0 ? "None." : Names(failed.Select(j => j.Name).ToList()),
                               "Agent Jobs →", "AgentJobs");
        }
        catch (Exception ex)
        {
            if (version != _sinceVersion)
                return;
            AppLog.Error("Overview: could not read the Agent jobs", ex);
            _failedJobs = new SinceRow("—", Muted, "Failed Agent jobs", $"Couldn't read them: {ErrorStatus.Format(ex)}", "Agent Jobs →", "AgentJobs");
        }
        RenderSince();
    }

    private static SinceRow RegressionsRow(int count, RegressionCriteria criteria) =>
        new(Count(count), CountBrush(count, HealthLevel.Warning), Plural(count, "Regressed query", "Regressed queries"),
            string.Format(CultureInfo.CurrentCulture,
                          "{0} more than {1:0.#} % slower than in the week before, from the monitoring history.",
                          count == 0 ? "None" : count == 1 ? "1 query" : $"{count} queries", criteria.ThresholdPct),
            "Query Regressions →", "QueryRegressions");

    // "Latest at 13:42:10: victim SPID 55 · app_login · dbo.usp_Transfer", or the objects fought over.
    private static string Latest(DeadlockReport report)
    {
        var what = report.VictimText.Length > 0 ? $"victim {report.VictimText}" : report.ObjectsText;
        return what.Length == 0 ? $"Latest at {When(report.TimestampUtc)}." : $"Latest at {When(report.TimestampUtc)}: {what}";
    }

    // "Nightly ETL, Backup logs and 3 more".
    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(2))} and {names.Count - 2} more";

    private static string Count(int count) => count.ToString(CultureInfo.CurrentCulture);

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private static Brush CountBrush(int count, HealthLevel level) => count == 0 ? HealthBrushes.Green : HealthBrushes.For(level);

    private static Brush Muted => HealthBrushes.Grey;

    // ── Live updates ──────────────────────────────────────────────────────────

    private void OnSessionStateChanged(Guid connectionId)
    {
        if (connectionId != _id)
            return;

        var newSample = TakeLiveScore();
        Render();
        _shell.ReportContextChanged(this);
        if (newSample)
        {
            _shell.ReportRead(this, PageRead.Timed);
            _ = RefreshBlockingAsync();
            // The first sample since the page opened or the server came back: now it can be asked.
            if (!_sinceRead && _monitoring.Get(_id) is { FailingSince: null })
                SinceLoaded = RefreshSinceAsync();
        }
    }

    // Started or stopped, this server or another: what can be read from this one changes with its own.
    private void OnMonitoringChanged()
    {
        Render();
        _shell.ReportContextChanged(this);
        if (_monitoring.Get(_id) is { IsRunning: true } != _sinceMonitored)
            SinceLoaded = RefreshSinceAsync();
    }

    private void OnAlertsChanged(Guid connectionId, IReadOnlyList<AlertChange> changes)
    {
        if (connectionId != _id)
            return;
        ViewModel.ReadSoon(ReloadAlertsDelay);
    }

    // Renamed, removed, made active, or its thresholds changed.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => Dispatcher.InvokeAsync(() =>
    {
        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        Render();
    });

    // A past range, or Live again, from the top bar.
    // Connected from XAML by name: a generic event handler is attached through reflection.
    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private async void RangePicker_RangeChanged(object? sender, HistoryRange range)
    {
        _range   = range;
        _metrics = null;
        _shell.ReportContextChanged(this);
        try
        {
            await LoadAsync(sinceToo: false);
        }
        catch (Exception ex)
        {
            if (_range == range)
                _shell.ReportError(this, "Overview history", ex, PageRead.Timed);
        }
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    private void Render()
    {
        if (_conn is not { } conn)
        {
            TxtName.Text = "Removed connection";
            TxtMissing.Visibility = Visibility.Visible;
            Main.Visibility = StatusChip.Visibility = ActiveChip.Visibility = Visibility.Collapsed;
            BtnMonitor.Visibility = BtnEdit.Visibility = BtnMakeActive.Visibility = Visibility.Collapsed;
            Dot.Fill = HealthBrushes.Grey;
            TxtConnectionLine.Text = string.Empty;
            return;
        }

        var session = _monitoring.Get(_id);
        var tile    = ServerTile.From(HomePage.InputFor(conn, _store, _monitoring), DateTime.Now);
        var running = session is { IsRunning: true };
        var score   = running ? session!.Score : null;

        Title = TxtName.Text      = conn.DisplayName;
        Dot.Fill                  = HealthBrushes.For(tile.Status);
        StatusChip.Background     = HealthBrushes.Tint(tile.Status);
        TxtStatus.Text            = tile.StatusLabel;
        ActiveChip.Visibility     = tile.IsActive ? Visibility.Visible : Visibility.Collapsed;
        BtnMakeActive.Visibility  = tile.IsActive ? Visibility.Collapsed : Visibility.Visible;
        TxtConnectionLine.Text    = $"{conn.Server.Trim()} / {conn.DatabaseLabel} · {conn.AuthenticationLabel} · " +
                                    (!running ? "not monitored"
                                     : tile.IsActive ? "monitoring"
                                     : "monitoring in the background") +
                                    (score is { Level: not HealthLevel.Unavailable } ? $" · latest sample {score.Time:HH:mm:ss}" : string.Empty);

        BtnMonitor.Content   = running ? "Pause monitoring" : "Start monitoring";
        BtnMonitor.IsEnabled = running || _monitoring.CanStart(_id);
        BtnMonitor.ToolTip   = running
            ? "Stop monitoring this connection until SqlVitals exits. Its active alerts end."
            : _monitoring.CanStart(_id)
                ? "Start monitoring this connection until SqlVitals exits, without switching to it."
                : $"Can't start: {_monitoring.NotMonitoredReason(_id)}";

        var unreachable = tile.Status == TileStatus.Unreachable;
        HealthRow.Visibility = unreachable ? Visibility.Collapsed : Visibility.Visible;
        OutageRow.Visibility = unreachable ? Visibility.Visible : Visibility.Collapsed;

        RenderAlerts(running, unreachable);
        if (unreachable)
            RenderOutage(conn, session!, tile);
        else
        {
            RenderScore(conn, tile, score);
            RenderHeadlines(conn, session);
            RenderSince();
        }
        RenderCharts();
    }

    private void RenderAlerts(bool running, bool unreachable)
    {
        var nowUtc   = DateTime.UtcNow;
        var todayUtc = DateTime.Today.ToUniversalTime();

        // The session's are the active ones: one may not be saved yet, and one the history still has
        // open may have ended a moment ago. Stopping monitoring ends them all.
        var byId = _alerts.ToDictionary(a => a.Id);
        foreach (var live in _monitoring.Get(_id)?.ActiveAlerts ?? [])
            byId[live.Id] = live;

        var active = (running ? _monitoring.Get(_id)!.ActiveAlerts : [])
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.StartedUtc)
            .Select(a =>
            {
                var evidence = EvidenceRoutes.For(a.Indicator);
                return new OverviewAlert(
                    HealthBrushes.For(a.Severity),
                    $"{HealthThresholds.Info(a.Indicator).Name} · {AlertText.Severity(a.Severity)}",
                    $"· since {Clock(a.StartedUtc)} · {AlertText.Duration(a.Duration(nowUtc))} so far",
                    AlertText.Happening(a),
                    "Investigate →",
                    $"Open {evidence.Text} for this server, filtered to this alert",
                    a);
            })
            .ToList();
        var endedToday = byId.Values.Count(a => a.EndedUtc >= todayUtc);

        Show(AlertsList, active);
        AlertsCard.BorderBrush = active.Count == 0 ? (Brush)FindResource("Border") : HealthBrushes.For(active.Max(a => a.Severity));
        TxtAlertsTitle.Text    = active.Count switch
        {
            0     => "Active alerts",
            1     => "1 active alert",
            var n => $"{n} active alerts",
        };
        TxtNoAlerts.Text = _historyProblem
            ?? (!running ? "None: it isn't monitored, so no check is running."
                : unreachable ? "None: the server can't be reached, so no check can run."
                : "None: no check has stayed past its threshold long enough to alert.") +
               (endedToday switch { 0 => "", 1 => " 1 alert ended earlier today.", var n => $" {n} alerts ended earlier today." });
        TxtNoAlerts.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderScore(ConnectionSettings conn, ServerTile tile, HealthScore? score)
    {
        var brush = HealthBrushes.For(tile.Status);
        RunScore.Text       = tile.ScoreText;
        RunScore.Foreground = brush;
        TxtLevel.Text       = tile.Status switch
        {
            TileStatus.Off     => "Not monitored",
            TileStatus.Waiting => "Waiting for the first sample",
            _                  => ServerTile.Label(tile.Status),
        };
        TxtLevel.Foreground = brush;
        TxtContext.Text     = score is null
            ? tile.Footer
            : ScoreTrend.Context(ScoreTrend.Merge(_todayScores, _live.Where(p => p.TimeUtc >= DateTime.Today.ToUniversalTime()).ToList()),
                                 score.Value, Clock);
        TxtContext.Visibility = TxtContext.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var graded = score is { Level: not HealthLevel.Unavailable, Checks.Count: > 0 } ? score : null;
        var costs  = graded is null ? [] : ScoreCosts.Lowering(graded);
        Show(CostsList, costs.Select(c =>
        {
            var evidence = EvidenceRoutes.For(c.Indicator);
            var detail   = graded!.Checks.FirstOrDefault(check => check.Indicator == c.Indicator)?.Reading.Reason;
            return new CostRow(HealthBrushes.For(c.Level), c.Name, $"−{c.Points}", c.Why, $"{evidence.Text} →",
                               $"Open {evidence.Text} for this server", c.Indicator, detail);
        }).ToList());
        TxtNoCosts.Text = graded is null
            ? (tile.Status == TileStatus.Off ? "Nothing to show: it isn't monitored." : "The checks show after the first sample.")
            : "Nothing: every check is within its thresholds.";
        TxtNoCosts.Visibility = costs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var passing = graded is null ? [] : ScoreCosts.Passing(graded).Select(p => $"{p.Name} · {p.ValueText}").ToList();
        Show(PassingList, passing);
        TxtPassingTitle.Visibility = passing.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        RunThresholds.Text = conn.HealthThresholds is null
            ? $"{conn.DisplayName} uses the default thresholds. "
            : $"{conn.DisplayName} uses its own thresholds. ";

        var blocking = score?.Checks.FirstOrDefault(c => c.Indicator == HealthIndicator.Blocking);
        if (blocking is not { Level: HealthLevel.Warning or HealthLevel.Critical })
            BlockingCard.Visibility = Visibility.Collapsed;
    }

    // CPU, batch requests/s, waiting tasks and page life expectancy from the session's samples.
    private void RenderHeadlines(ConnectionSettings conn, MonitoringSession? session)
    {
        var samples    = session is { IsRunning: true } ? Plotted(session) : [];
        var thresholds = session?.Thresholds ?? _store.ThresholdsFor(conn);
        var primary    = (Brush)FindResource("TextPrimary");

        HeadlinesList.ItemsSource = Headlines.From(samples, thresholds).Select(h => new HeadlineTile(
            h.Label,
            h.ValueText,
            h.Level is { } level ? HealthBrushes.For(level) : primary,
            h.Level is { } line ? HealthBrushes.For(line) : null,
            h.Trend,
            h.Note,
            HeadlineToolTip(h.Kind, samples.Count))).ToList();
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

    private void RenderSince()
    {
        var nowUtc = DateTime.UtcNow;
        TxtSinceWhen.Text = Look switch
        {
            { IsFirstLook: true }                       => "In the last 24 hours: this is the first time this Overview has been opened.",
            { Capped: true, LastLookedUtc: { } last }   => $"In the last 7 days. You last looked {When(last)}.",
            { LastLookedUtc: { } last }                 => $"Since {When(last)}, {AlertText.Duration(nowUtc - last)} ago.",
            _                                           => string.Empty,
        };
        Show(SinceList, new[]
        {
            _deadlocks   ?? new SinceRow("…", Muted, "Deadlocks", "Reading…", "Deadlocks →", "Deadlocks"),
            _failedJobs  ?? new SinceRow("…", Muted, "Failed Agent jobs", "Reading…", "Agent Jobs →", "AgentJobs"),
            _regressions ?? new SinceRow("…", Muted, "Regressed queries", "Reading the monitoring history…", "Query Regressions →", "QueryRegressions"),
        });
    }

    // Replaces a list's rows only when they changed, so redrawing on every sample doesn't take
    // the keyboard focus off a button in it (#140 learned this of the breadcrumb).
    private static void Show<T>(ItemsControl list, IReadOnlyList<T> rows)
    {
        if (list.ItemsSource is IEnumerable<T> shown && shown.SequenceEqual(rows))
            return;
        list.ItemsSource = rows;
    }

    // ── Charts ────────────────────────────────────────────────────────────────

    // The theme's colours by meaning, the same as Live charts' (#164).
    private Brush WaitCpu      => Theme("WaitCpu");
    private Brush WaitIo       => Theme("WaitIo");
    private Brush WaitLock     => Theme("WaitLock");
    private Brush WaitMemory   => Theme("WaitMemory");
    private Brush WaitNetwork  => Theme("WaitNetwork");
    private Brush Batches      => Theme("MetricBatches");
    private Brush Transactions => Theme("MetricTransactions");
    private Brush Ple          => Theme("MetricPle");
    private Brush Reads        => Theme("MetricReads");
    private Brush Writes       => Theme("MetricWrites");

    private Brush Theme(string brushKey) => (Brush)FindResource(brushKey);

    private Brush Accent => TryFindResource("Accent") as Brush ?? Brushes.MediumPurple;

    // Each chart's title, with a coloured dot per line.
    private void BuildLegends()
    {
        Legend(LegendCpu, "CPU (%)", ("SQL Server", Accent));
        // Other leaves out what the categories don't cover, as Live charts does by default (#136).
        Legend(LegendWaits, "Waits (ms/s)", ("CPU", WaitCpu), ("I/O", WaitIo), ("Locks", WaitLock), ("Memory", WaitMemory), ("Network", WaitNetwork));
        Legend(LegendThroughput, "Throughput (/s)", ("Batch requests", Batches), ("Transactions", Transactions));
        Legend(LegendPle, "Page life expectancy (s)", ("PLE", Ple));
        Legend(LegendIo, "Physical I/O (pages/s)", ("Reads", Reads), ("Writes", Writes));
    }

    private static void Legend(TextBlock block, string title, params (string Name, Brush Brush)[] series)
    {
        block.Inlines.Clear();
        block.Inlines.Add(new Run(title));
        if (series.Length < 2)
            return;
        foreach (var (name, brush) in series)
        {
            block.Inlines.Add(new Run("   ●") { Foreground = brush, FontWeight = FontWeights.Normal });
            block.Inlines.Add(new Run($" {name}") { FontWeight = FontWeights.Normal, FontSize = 11 });
        }
    }

    private void RenderCharts()
    {
        var nowUtc = DateTime.UtcNow;
        var (fromUtc, toUtc, bucket) = _range.IsLive ? LiveWindow(nowUtc)
                                     : _metrics is { } load ? (load.FromUtc, load.ToUtc, load.Bucket)
                                     : PastWindow(_range, nowUtc);

        // Live: the session's samples, put on this PC's clock; a past range: the history's averages.
        var rows = _range.IsLive ? LiveSamples() : _metrics?.Items.Select(p => (p.TimeUtc, p.Averages)).ToList() ?? [];
        MetricSeries Series(string name, Brush brush, Func<LiveMetricSample, double> value) =>
            new(name, brush, rows.Select(r => new MetricPoint(r.TimeUtc, value(r.Sample))).ToList());

        var markers = _alerts
            .Concat(_monitoring.Get(_id)?.ActiveAlerts ?? [])
            .DistinctBy(a => a.Id)
            .Where(a => a.StartedUtc >= fromUtc && a.StartedUtc <= toUtc)
            .Select(a => new ChartMarker(a.StartedUtc, $"{Clock(a.StartedUtc)} {ServerTile.ShortName(a.Indicator)}", HealthBrushes.For(a.Severity)))
            .ToList();

        var scores = ScoreTrend.Merge(_windowScores, _live.Where(p => p.TimeUtc >= fromUtc).ToList());
        ChartScore.Data = new ScoreChartData(scores, fromUtc, toUtc, bucket, markers);
        ChartCpu.Data   = new MetricChartData([Series("SQL Server CPU", Accent, s => s.SqlCpuPct)], fromUtc, toUtc, bucket, markers, Top: 100);
        ChartWaits.Data = new MetricChartData(
        [
            Series("CPU",     WaitCpu,     s => s.WaitCpuMsPerSec),
            Series("I/O",     WaitIo,      s => s.WaitIoMsPerSec),
            Series("Locks",   WaitLock,    s => s.WaitLockMsPerSec),
            Series("Memory",  WaitMemory,  s => s.WaitMemoryMsPerSec),
            Series("Network", WaitNetwork, s => s.WaitNetworkMsPerSec),
        ], fromUtc, toUtc, bucket, markers);
        ChartThroughput.Data = new MetricChartData(
        [
            Series("Batch requests", Batches, s => s.BatchRequestsPerSec),
            Series("Transactions",   Transactions, s => s.TransactionsPerSec),
        ], fromUtc, toUtc, bucket, markers);
        ChartPle.Data = new MetricChartData([Series("Page life expectancy", Ple, s => s.PageLifeExpectancySec)], fromUtc, toUtc, bucket, markers);
        ChartIo.Data  = new MetricChartData(
        [
            Series("Reads",  Reads,  s => s.PhysicalReadsPerSec),
            Series("Writes", Writes, s => s.PhysicalWritesPerSec),
        ], fromUtc, toUtc, bucket, markers);

        var running = _monitoring.Get(_id) is { IsRunning: true };
        TxtChartsTitle.Text = _range.IsLive ? "Live" : _range.Label;
        TxtChartsNote.Text  = _historyProblem ?? (_range.IsLive
            ? (running
                ? $"The last {Span(toUtc - fromUtc)}, a sample every {ConnectionHistory.FormatBucket(bucket)}. "
                : "Not monitored: nothing new is collected. Pick a past range in the top bar to see the history. ")
            : rows.Count == 0
                ? "Nothing recorded in this range: metrics are recorded while SqlVitals monitors this connection. "
                : $"Averages per {ConnectionHistory.FormatBucket(bucket)} from the monitoring history. ")
            + (markers.Count > 0 ? "Dashed lines: alerts starting. " : string.Empty)
            + "Shaded: the server couldn't be reached. Times on this PC's clock.";

        AutomationProperties.SetName(ChartScore, scores.Count == 0
            ? "Health score: no scores recorded in this window"
            : $"Health score: lowest {scores.Min(p => p.Score)}, latest {scores[^1].Score}");
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

    // ── The server can't be reached (#130) ────────────────────────────────────

    private void RenderOutage(ConnectionSettings conn, MonitoringSession session, ServerTile tile)
    {
        var (host, _, _) = ThingsToCheck.Parse(conn.Server);
        TxtOutageTitle.Text = $"SqlVitals can't reach {host}";
        TxtOutageError.Text = session.LastError ?? tile.ReasonToolTip ?? "The last collection failed.";
        ThingsList.ItemsSource = ThingsToCheck.For(conn.Server, session.LastError);
        RenderOutageCountdown();

        // From this run when a sample worked in it; otherwise the history knows the last one.
        var lastGood = session.LastGoodScore is { } good
            ? new LastGood(good.Time.ToUniversalTime(), good.Value, good.Lowering.Count == 0 ? null : good.LoweredBy())
            : _lastReachable is { } stored ? new LastGood(stored.TimeUtc, stored.Score, null) : null;
        TxtBefore.Text = lastGood is null
            ? "No sample has worked since monitoring started."
            : $"Score {lastGood.Score} at {When(lastGood.TimeUtc)}" +
              (lastGood.LoweredBy is { } why ? $", lowered by {why}."
               : lastGood.Score == HealthScore.Max ? ": every check within its thresholds."
               : ".");

        var failingSinceUtc = (session.FailingSince ?? DateTime.Now).ToUniversalTime();
        Timeline.ItemsSource = OutageTimeline.Build(
                lastGood is null ? null : (lastGood.TimeUtc, lastGood.Score),
                failingSinceUtc,
                session.ConsecutiveFailures,
                session.IsRunning ? DateTime.UtcNow + session.TimeUntilNextSample : null,
                _activity,
                _alerts.Concat(session.ActiveAlerts).DistinctBy(a => a.Id)
                       .Where(a => a.StartedUtc >= failingSinceUtc.AddHours(-2) || a.EndedUtc >= failingSinceUtc.AddHours(-2)),
                DateTime.UtcNow)
            .Select(e => new DetailLine(Clock(e.TimeUtc), e.Text))
            .ToList();
    }

    // "Since 21:41:05 · 28 failed attempts · next try in 12 s", kept counting down.
    private void RenderOutageCountdown()
    {
        if (OutageRow.Visibility != Visibility.Visible || _monitoring.Get(_id) is not { } session)
            return;

        var attempts = session.ConsecutiveFailures == 1 ? "1 failed attempt" : $"{session.ConsecutiveFailures} failed attempts";
        TxtOutageSince.Text = (session.FailingSince is { } since ? $"Since {When(since.ToUniversalTime())} · " : string.Empty) + attempts +
                              (session.IsRunning ? $" · next try in {AlertText.Duration(session.TimeUntilNextSample)}" : " · monitoring is stopped");
    }

    // ── Blocking right now ────────────────────────────────────────────────────

    // Reads the sessions from the server, once per sample at most, while blocking is past its threshold.
    private async Task RefreshBlockingAsync()
    {
        var session  = _monitoring.Get(_id);
        var blocking = session is { IsRunning: true } ? session.Score?.Checks.FirstOrDefault(c => c.Indicator == HealthIndicator.Blocking) : null;
        var show     = blocking is { Level: HealthLevel.Warning or HealthLevel.Critical } && HealthRow.Visibility == Visibility.Visible;
        if (show && BlockingCard.Visibility != Visibility.Visible)
        {
            // Nothing from an earlier spell of blocking.
            BlockingGrid.Visibility = Visibility.Collapsed;
            TxtBlockingFor.Text     = string.Empty;
            TxtBlockingNote.Text    = "Reading the sessions…";
        }
        BlockingCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || _blockingLoading || !IsLoaded)
            return;

        _blockingLoading = true;
        try
        {
            var now = BlockingNow.From(await session!.Repository.GetProcessesAsync());
            BlockingGrid.Visibility = now is null ? Visibility.Collapsed : Visibility.Visible;
            if (now is null)
            {
                TxtBlockingFor.Text  = string.Empty;
                TxtBlockingNote.Text = "No blocking chain is visible now: it may have just cleared.";
                return;
            }

            TxtBlockingFor.Text  = AlertText.Duration(now.LongestWait).ToUpper(CultureInfo.CurrentCulture);
            TxtHead.Text         = $"Session {now.HeadSessionId} · {now.Who}";
            TxtWaiting.Text      = now.WaitingText;
            TxtStatement.Text    = now.Statement ?? (now.HeadIsSleeping ? "None: the session is idle, holding a transaction open." : "None");
            TxtBlockingNote.Text = now.OtherChains switch
            {
                0     => string.Empty,
                1     => "…and 1 more blocking chain.",
                var n => $"…and {n} more blocking chains.",
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("Overview: could not read the blocking sessions", ex);
            BlockingGrid.Visibility = Visibility.Collapsed;
            TxtBlockingNote.Text    = $"Couldn't read the sessions: {ErrorStatus.Format(ex)}";
        }
        finally
        {
            _blockingLoading = false;
        }
    }

    // ── Buttons and links ─────────────────────────────────────────────────────

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (_monitoring.IsMonitoring(_id))
            _monitoring.Stop(_id, ActivitySources.Overview);
        else
            _monitoring.Start(_id, ActivitySources.Overview);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_monitoring.Get(_id) is not { } session)
            return;

        BtnRetry.IsEnabled = false;
        ShowOutageAction("Trying to reach it…");
        try
        {
            await session.CollectNowAsync();
            ShowOutageAction("Reached it: the sample worked.");
        }
        catch
        {
            ShowOutageAction($"Still can't reach it at {DateTime.Now:HH:mm:ss}.");
        }
        finally
        {
            BtnRetry.IsEnabled = true;
            Render();
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_conn is not { } conn)
            return;
        if (conn.NeedsPassword)
        {
            ShowOutageAction("The password isn't saved. Enter it with Edit connection.");
            return;
        }

        BtnTest.IsEnabled = false;
        ShowOutageAction("Testing the connection…");
        try
        {
            var error = await ConnectionTest.TryOpenAsync(conn.ConnectionString);
            ShowOutageAction(error is null
                ? "Connection successful: the server answers. The next sample should work; Retry now takes it straight away."
                : $"Connection failed: {error}");
        }
        finally
        {
            BtnTest.IsEnabled = true;
        }
    }

    private void ShowOutageAction(string text)
    {
        TxtOutageAction.Text       = text;
        TxtOutageAction.Visibility = Visibility.Visible;
    }

    private void CopyError_Click(object sender, RoutedEventArgs e) => ClipboardHelper.SetText(TxtOutageError.Text);

    private void Edit_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(_id);

    private void ChangeThresholds_Click(object sender, RoutedEventArgs e) => ThresholdsRequested?.Invoke(_id);

    private void MakeActive_Click(object sender, RoutedEventArgs e) => MakeActiveRequested?.Invoke(_id);

    private void AllAlerts_Click(object sender, RoutedEventArgs e) => AlertsRequested?.Invoke(_id);

    private void LiveCharts_Click(object sender, RoutedEventArgs e) => OpenPageRequested?.Invoke(_id, "LiveMetrics");

    private void BlockingChain_Click(object sender, RoutedEventArgs e) =>
        EvidenceRequested?.Invoke(EvidenceRoutes.ForCheck(HealthIndicator.Blocking, _id, detail: null));

    // An alert's evidence (#145): its page, filtered to the problem and set to the alert's time.
    private void Evidence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverviewAlert row })
            EvidenceRequested?.Invoke(EvidenceRoutes.ForAlert(row.Alert));
    }

    // A check costing points: its page, filtered as an alert's would be, as the server is now.
    private void CostLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkContentElement { DataContext: CostRow cost })
            EvidenceRequested?.Invoke(EvidenceRoutes.ForCheck(cost.Indicator, _id, cost.Detail));
    }

    private void SinceLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkContentElement { DataContext: SinceRow row })
            OpenPageRequested?.Invoke(_id, row.PageTag);
    }

    // ── Times ─────────────────────────────────────────────────────────────────

    // "21:52" on this PC's clock.
    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    // "21:52:10" today, "30 Sep 21:52" on another day, on this PC's clock.
    private static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }
}

/// <summary>The last score taken while the server could be reached, and what lowered it if known.</summary>
internal sealed record LastGood(DateTime TimeUtc, int Score, string? LoweredBy);

/// <summary>A line of "What happened": its time, and what.</summary>
public sealed record DetailLine(string When, string Text);

/// <summary>An active alert at the top of the Overview: what, since when, in plain words, and Investigate.</summary>
public sealed record OverviewAlert(Brush Dot, string Title, string When, string Happening, string EvidenceText, string EvidenceToolTip,
                                   Alert Alert)
{
    public HealthIndicator Indicator => Alert.Indicator;
    public HealthLevel     Severity  => Alert.Severity;
}

/// <summary>A check costing points: its name, "−20", why, and the page that explains it.</summary>
/// <param name="Detail">What the check said, e.g. "Log 93% full (Sales)": its evidence's filter.</param>
public sealed record CostRow(Brush Dot, string Name, string Points, string Why, string LinkText, string LinkToolTip, HealthIndicator Indicator,
                             string? Detail = null);

/// <summary>A headline number with its sparkline.</summary>
public sealed record HeadlineTile(string Label, string Value, Brush ValueBrush, Brush? LineBrush, IReadOnlyList<double> Trend,
                                  string Note, string ToolTip);

/// <summary>One line of "Since you last looked": how many, of what, a detail, and the page with the rest.</summary>
public sealed record SinceRow(string Count, Brush CountBrush, string What, string Detail, string LinkText, string PageTag);
