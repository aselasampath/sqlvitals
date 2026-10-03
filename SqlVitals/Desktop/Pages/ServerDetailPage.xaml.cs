using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// One server, opened from its Home tile (#130): its score now and over time, the checks with a
/// link to the page that explains each one losing points, the blocking chain while blocking is
/// past its threshold, and its alerts. While the server can't be reached: why, what to check, and
/// what happened. Only "Blocking right now" queries the server, and only while it's shown.
/// </summary>
public partial class ServerDetailPage : Page, IRefreshable, IPageContext
{
    // The server's health as its samples arrive (#140), at its monitoring interval.
    public PageKind Kind => PageKind.Live;

    // The server's monitoring interval while it is monitored; no interval to show while it isn't.
    public TimeSpan? LiveInterval => _monitoring.Get(_id) is { IsRunning: true }
        ? TimeSpan.FromSeconds(_monitoring.IntervalFor(_id))
        : null;

    public bool IsPaused => _monitoring.Get(_id) is not { IsRunning: true };

    private const int MaxAlerts = 8;

    private readonly Guid                      _id;
    private readonly MonitoringManager         _monitoring;
    private readonly ConnectionSettingsService _settings;

    // Ticks the "next try in" countdown while the server can't be reached.
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    // An alert change is queued to the history writer; reading straight away could miss it.
    private readonly DispatcherTimer _reloadAlertsSoon = new() { Interval = TimeSpan.FromSeconds(1) };

    private ConnectionStore     _store;
    private ConnectionSettings? _conn;
    private HistoryRange        _range = HistoryRange.Last24Hours;
    private bool                _ready;

    // From the history, and the scores taken since the page opened.
    private IReadOnlyList<ScorePoint> _rangeScores = [];
    private IReadOnlyList<ScorePoint> _todayScores = [];
    private readonly List<ScorePoint> _live = new();
    private IReadOnlyList<Alert>         _alerts   = [];
    private IReadOnlyList<ActivityEntry> _activity = [];
    private ScoreHistoryPoint?           _lastReachable;
    private string?                      _historyProblem;

    private DateTime? _lastScoreTime;
    private bool      _blockingLoading;
    private int       _loadVersion;

    public ServerDetailPage(Guid connectionId, MonitoringManager monitoring, ConnectionSettingsService settings)
    {
        _id         = connectionId;
        _monitoring = monitoring;
        _settings   = settings;
        InitializeComponent();

        Breakdown.ShowInline();
        Breakdown.OpenCheck = indicator => OpenPageRequested?.Invoke(_id, CheckLinks.For(indicator).PageTag);
        Breakdown.Footnote  = "Each check is worth its weight in points. Past its warning threshold it loses half; past critical, all of it.";
        Range24h.IsChecked  = true;

        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        TakeLiveScore();
        Render();

        _tick.Tick             += (_, _) => RenderOutageCountdown();
        _reloadAlertsSoon.Tick += async (_, _) => { _reloadAlertsSoon.Stop(); await RefreshQuietlyAsync(); };
        Loaded += (_, _) =>
        {
            _monitoring.SessionStateChanged += OnSessionStateChanged;
            _monitoring.MonitoringChanged   += OnMonitoringChanged;
            _monitoring.AlertsChanged       += OnAlertsChanged;
            _settings.Changed               += OnSettingsChanged;
            _tick.Start();
            // MainWindow loads the page before it's shown, when the sessions can't be read yet.
            _ = RefreshBlockingAsync();
        };
        Unloaded += (_, _) =>
        {
            _monitoring.SessionStateChanged -= OnSessionStateChanged;
            _monitoring.MonitoringChanged   -= OnMonitoringChanged;
            _monitoring.AlertsChanged       -= OnAlertsChanged;
            _settings.Changed               -= OnSettingsChanged;
            _tick.Stop();
            _reloadAlertsSoon.Stop();
        };
        _ready = true;
    }

    /// <summary>"← Your servers".</summary>
    public event Action? BackRequested;

    /// <summary>A page about this connection, e.g. Processes from "Blocking chain": it's made active first.</summary>
    public event Action<Guid, string>? OpenPageRequested;

    /// <summary>Make active: switch the app to this connection, as the sidebar selector does.</summary>
    public event Action<Guid>? MakeActiveRequested;

    /// <summary>Edit connection: open it in Settings.</summary>
    public event Action<Guid>? EditRequested;

    /// <summary>Change, under the checks: its Health thresholds tab in Settings (#131).</summary>
    public event Action<Guid>? ThresholdsRequested;

    /// <summary>All alerts: the Alerts page, filtered to this connection.</summary>
    public event Action<Guid>? AlertsRequested;

    public Guid ConnectionId => _id;

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>Reads the score history, today's alerts and, while it can't be reached, what happened.</summary>
    public async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        Render();
        if (_conn is null)
            return;

        var reader  = _monitoring.HistoryReader;
        var nowUtc  = DateTime.UtcNow;
        var (fromUtc, toUtc) = _range.Resolve(nowUtc);
        var bucket   = HistoryRange.BucketFor(toUtc - fromUtc);
        var todayUtc = DateTime.Today.ToUniversalTime();
        var session  = _monitoring.Get(_id);
        var failing  = session?.FailingSince?.ToUniversalTime();

        // A minute ahead, so a score or alert from a sample just taken is in range.
        var ahead = nowUtc.AddMinutes(1);
        try
        {
            var data = await Task.Run(() => (
                Range:     reader.ReadScores(_id, fromUtc, toUtc.AddMinutes(1), bucket).Select(ScorePoint.From).ToList(),
                Today:     reader.ReadScores(_id, todayUtc, ahead, TimeSpan.FromMinutes(1)).Select(ScorePoint.From).ToList(),
                Alerts:    reader.ReadAlerts(_id, fromUtc < todayUtc ? fromUtc : todayUtc, ahead).Select(a => a.Alert).ToList(),
                Activity:  failing is { } f ? reader.ReadActivity(f.AddHours(-2), ahead, _id).Select(r => r.Entry).ToList() : [],
                Reachable: failing is { } since ? reader.ReadLastReachableScore(_id, since) : null));
            if (version != _loadVersion)
                return;

            (_rangeScores, _todayScores, _alerts, _activity, _lastReachable) =
                (data.Range, data.Today, data.Alerts, data.Activity, data.Reachable);
            _historyProblem = null;
        }
        catch (HistorySchemaTooNewException)
        {
            _historyProblem = "The history file is from a newer SqlVitals, so only what was collected since this page opened is shown.";
        }

        if (version != _loadVersion)
            return;
        Render();
        await RefreshBlockingAsync();
    }

    private async Task RefreshQuietlyAsync()
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { AppLog.Error("Server detail: load failed", ex); }
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

    // ── Live updates ──────────────────────────────────────────────────────────

    private void OnSessionStateChanged(Guid connectionId)
    {
        if (connectionId != _id)
            return;

        var newSample = TakeLiveScore();
        Render();
        MainWindow.ReportContextChanged(this);
        if (newSample)
        {
            MainWindow.ReportBackgroundSuccess(this);
            _ = RefreshBlockingAsync();
        }
    }

    private void OnMonitoringChanged()
    {
        Render();
        MainWindow.ReportContextChanged(this);
    }

    private void OnAlertsChanged(Guid connectionId, IReadOnlyList<AlertChange> changes)
    {
        if (connectionId != _id)
            return;
        _reloadAlertsSoon.Stop();
        _reloadAlertsSoon.Start();
    }

    // Renamed, removed, made active, or its thresholds changed.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => Dispatcher.InvokeAsync(() =>
    {
        _store = _settings.Load();
        _conn  = _store.Connections.FirstOrDefault(c => c.Id == _id);
        Render();
    });

    // ── Drawing ───────────────────────────────────────────────────────────────

    private void Render()
    {
        if (_conn is not { } conn)
        {
            RunCrumb.Text = TxtName.Text = "Removed connection";
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

        Title = RunCrumb.Text = TxtName.Text = conn.DisplayName;
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
        ScoreCard.Visibility = ChecksRow.Visibility = unreachable ? Visibility.Collapsed : Visibility.Visible;
        OutageRow.Visibility = unreachable ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(TrendCard, unreachable ? 1 : 0);
        Grid.SetColumn(TrendCard, unreachable ? 0 : 1);
        Grid.SetColumnSpan(TrendCard, unreachable ? 2 : 1);

        if (unreachable)
            RenderOutage(conn, session!, tile);
        else
        {
            RenderScore(tile, score);
            RenderChecks(conn, tile, score);
            RenderAlerts(conn);
        }
        RenderTrend(conn);
    }

    private void RenderScore(ServerTile tile, HealthScore? score)
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
        TxtReason.Text      = tile.Status == TileStatus.Off ? tile.Footer : tile.Reason;
        TxtContext.Text     = score is null
            ? string.Empty
            : ScoreTrend.Context(ScoreTrend.Merge(_todayScores, _live.Where(p => p.TimeUtc >= DateTime.Today.ToUniversalTime()).ToList()),
                                 score.Value, Clock);
    }

    private void RenderChecks(ConnectionSettings conn, ServerTile tile, HealthScore? score)
    {
        if (score is { Checks.Count: > 0 })
        {
            Breakdown.Show(conn.DisplayName, score, string.Empty, paused: false);
            Breakdown.Visibility  = Visibility.Visible;
            TxtNoChecks.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtNoChecks.Text = tile.Status == TileStatus.Off
                ? "No checks: it isn't monitored."
                : "The checks show after the first sample.";
            TxtNoChecks.Visibility = Visibility.Visible;
            Breakdown.Visibility   = Visibility.Collapsed;
        }

        RunThresholds.Text = conn.HealthThresholds is null
            ? $"{conn.DisplayName} uses the default thresholds. "
            : $"{conn.DisplayName} uses its own thresholds. ";

        var blocking = score?.Checks.FirstOrDefault(c => c.Indicator == HealthIndicator.Blocking);
        if (blocking is not { Level: HealthLevel.Warning or HealthLevel.Critical })
            BlockingCard.Visibility = Visibility.Collapsed;
    }

    private void RenderAlerts(ConnectionSettings conn)
    {
        var nowUtc   = DateTime.UtcNow;
        var todayUtc = DateTime.Today.ToUniversalTime();

        // The session knows the active alerts best: one may not be saved yet, or may have changed since.
        var byId = _alerts.ToDictionary(a => a.Id);
        foreach (var live in _monitoring.Get(_id)?.ActiveAlerts ?? [])
            byId[live.Id] = live;

        var rows = byId.Values
            .Where(a => a.IsActive || a.EndedUtc >= todayUtc)
            .OrderByDescending(a => a.IsActive)
            .ThenByDescending(a => a.StartedUtc)
            .Take(MaxAlerts)
            .Select(a => new DetailAlert(
                HealthBrushes.For(a.IsActive ? a.Severity : HealthLevel.Unknown),
                $"{HealthThresholds.Info(a.Indicator).Name}: {AlertText.Figure(a)}",
                AlertDetail(a, nowUtc) + Channels(a)))
            .ToList();

        TxtAlertsTitle.Text    = $"Alerts on {conn.DisplayName}";
        AlertsList.ItemsSource = rows;
        TxtNoAlerts.Text       = _historyProblem ?? "No alerts today.";
        TxtNoAlerts.Visibility = rows.Count == 0 || _historyProblem is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string AlertDetail(Alert a, DateTime nowUtc) =>
        a.EndedUtc is { } ended
            ? $"{AlertText.Severity(a.Severity)} · {Clock(a.StartedUtc)}–{Clock(ended)} · lasted {AlertText.Duration(a.Duration(nowUtc))}"
            : $"{AlertText.Severity(a.Severity)} · since {Clock(a.StartedUtc)} · {AlertText.Duration(a.Duration(nowUtc))} so far";

    // The chat channels whose settings take this alert (#121): its connection and severity.
    private string Channels(Alert a)
    {
        var names = _store.ChatChannels
            .Where(c => c.Enabled && c.Includes(_id) && (!c.CriticalOnly || a.Severity == HealthLevel.Critical))
            .Select(c => c.Name)
            .ToList();
        return names.Count == 0 ? string.Empty : $" · chat: {string.Join(", ", names)}";
    }

    private void RenderTrend(ConnectionSettings conn)
    {
        var nowUtc = DateTime.UtcNow;
        var (fromUtc, toUtc) = _range.Resolve(nowUtc);
        var bucket = HistoryRange.BucketFor(toUtc - fromUtc);
        var points = ScoreTrend.Merge(_rangeScores, _live.Where(p => p.TimeUtc >= fromUtc).ToList());

        var markers = _alerts
            .Concat(_monitoring.Get(_id)?.ActiveAlerts ?? [])
            .DistinctBy(a => a.Id)
            .Where(a => a.StartedUtc >= fromUtc)
            .Select(a => new ChartMarker(a.StartedUtc, $"{Clock(a.StartedUtc)} {ServerTile.ShortName(a.Indicator)}", HealthBrushes.For(a.Severity)))
            .ToList();

        Chart.Data = new ScoreChartData(points, fromUtc, toUtc, bucket, markers);

        var label = _range.Label.ToLower(CultureInfo.CurrentCulture).Replace("last ", string.Empty);
        TxtTrendTitle.Text = $"Score over the last {label}";
        TxtTrendNote.Text  = _historyProblem ?? (points.Count == 0
            ? "No scores in this range. A score is recorded with every sample while the connection is monitored."
            : $"The lowest score in each {ConnectionHistory.FormatBucket(bucket)}. Dashed lines: alerts starting. " +
              "Shaded: the server couldn't be reached. Times on this PC's clock.");
        AutomationProperties.SetName(Chart, points.Count == 0
            ? $"Health score over the last {label}: no scores recorded"
            : $"Health score over the last {label}: lowest {points.Min(p => p.Score)}, latest {points[^1].Score}");
    }

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
        var show     = blocking is { Level: HealthLevel.Warning or HealthLevel.Critical } && ChecksRow.Visibility == Visibility.Visible;
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
            AppLog.Error("Server detail: could not read the blocking sessions", ex);
            BlockingGrid.Visibility = Visibility.Collapsed;
            TxtBlockingNote.Text    = $"Couldn't read the sessions: {MainWindow.FormatErrorStatus(ex)}";
        }
        finally
        {
            _blockingLoading = false;
        }
    }

    // ── Buttons and links ─────────────────────────────────────────────────────

    private async void Range_Checked(object sender, RoutedEventArgs e)
    {
        _range = ReferenceEquals(sender, Range1h) ? HistoryRange.LastHour
               : ReferenceEquals(sender, Range7d) ? HistoryRange.Last7Days
               : HistoryRange.Last24Hours;
        if (_ready)
            await RefreshQuietlyAsync();
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (_monitoring.IsMonitoring(_id))
            _monitoring.Stop(_id, ActivitySources.Home);
        else
            _monitoring.Start(_id, ActivitySources.Home);
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

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void Edit_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(_id);

    private void ChangeThresholds_Click(object sender, RoutedEventArgs e) => ThresholdsRequested?.Invoke(_id);

    private void MakeActive_Click(object sender, RoutedEventArgs e) => MakeActiveRequested?.Invoke(_id);

    private void AllAlerts_Click(object sender, RoutedEventArgs e) => AlertsRequested?.Invoke(_id);

    private void BlockingChain_Click(object sender, RoutedEventArgs e) => OpenPageRequested?.Invoke(_id, CheckLinks.For(HealthIndicator.Blocking).PageTag);

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

/// <summary>An alert on the detail page: its dot, what, and when.</summary>
public sealed record DetailAlert(Brush Dot, string Title, string Detail);
