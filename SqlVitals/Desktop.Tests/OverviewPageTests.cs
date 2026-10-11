using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// A server's Overview (#143) as WPF builds it, off screen, over four saved connections: "Calm"
/// (healthy, with a deadlock and a failed job since it was last looked at), "Hot" (the active one,
/// CPU at 99 % for long enough to alert), "Down" (every collection fails) and "Quiet" (not monitored).
/// </summary>
public sealed class OverviewPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();

    private readonly ConnectionSettings _calm  = Conn("calm",  "Calm", background: true);
    private readonly ConnectionSettings _hot   = Conn("hot",   "Hot");
    private readonly ConnectionSettings _down  = Conn("down",  "Down", background: true);
    private readonly ConnectionSettings _quiet = Conn("quiet", "Quiet");

    // Looked at two hours ago.
    private readonly LookWindow _look = SinceLastLooked.Window(DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);

    private MonitoringManager?         _monitoring;
    private ConnectionSettingsService? _settings;

    private static ConnectionSettings Conn(string server, string name, bool background = false) => new()
    {
        Server              = server,
        Name                = name,
        Database            = "Sales",
        Authentication      = SqlAuthMode.Windows,
        MonitorInBackground = background,
    };

    private IWaitStatsRepository Repository(ConnectionSettings conn) => conn.Server switch
    {
        "hot"  => MonitoringManagerTests.Hot(conn.Server, _calls),
        "calm" => Calm(conn.Server, _calls),
        _      => MonitoringManagerTests.Counting(conn.Server, _calls),
    };

    // On the UI thread: the four connections saved, Hot active, monitoring started automatically;
    // three samples from Hot (enough for its CPU alert), one from Calm, and Down failing.
    private (MonitoringManager Monitoring, ConnectionSettingsService Settings) Setup()
    {
        _settings = new ConnectionSettingsService(_dir);
        foreach (var conn in new[] { _calm, _hot, _down, _quiet })
            _settings.Upsert(conn);
        _settings.SetActive(_hot.Id);
        _settings.SetStartMonitoringAutomatically(true);

        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                            (conn, _) => Repository(conn));
        _monitoring.Sync(_settings.Load(), ActivitySources.AutomaticAtStartup);

        for (var i = 0; i < 3; i++)
            UiThread.Wait(_monitoring.Get(_hot.Id)!.CollectNowAsync());
        UiThread.Wait(_monitoring.Get(_calm.Id)!.CollectNowAsync());
        try { UiThread.Wait(_monitoring.Get(_down.Id)!.CollectNowAsync()); }
        catch (InvalidOperationException) { /* "No server in tests.": Down can't be reached */ }

        return (_monitoring, _settings);
    }

    // A day of a connection's scores in the history (#170): every 5 minutes, except a 10-hour
    // night with none, ending a minute ago. Written before the monitoring opens the file.
    private void SeedADayWithANightOff(ConnectionSettings conn)
    {
        var now   = DateTime.UtcNow;
        var night = (From: now.AddHours(-20), To: now.AddHours(-10));
        var who   = new HistoryConnection(conn.Id, conn.DisplayName, conn.Server, "");
        var records = new List<HistoryRecord>();
        for (var t = now.AddHours(-24).AddMinutes(1); t < now; t = t.AddMinutes(5))
            if (t < night.From || t > night.To)
                records.Add(new HealthScoreRecord(who, t, 90, Reachable: true));

        Directory.CreateDirectory(_dir);
        using var store = new HistoryStore(Path.Combine(_dir, "history.db"));
        store.Open();
        store.Write(records);
    }

    // The page, laid out, loaded, and its server reads finished.
    private OverviewPage Open(ConnectionSettings conn)
    {
        var page = new OverviewPage(conn.Id, _monitoring!, _settings!, _look);
        UiThread.Layout(page, 1400, 1600);
        UiThread.Wait(page.RefreshAsync());
        UiThread.Wait(page.SinceLoaded);
        UiThread.Layout(page, 1400, 1600);
        return page;
    }

    private static List<T> Rows<T>(ItemsControl list) => list.ItemsSource.Cast<T>().ToList();

    private static void Click(Hyperlink link) => link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));

    private static IEnumerable<Hyperlink> Links(DependencyObject root) =>
        UiThread.Find<TextBlock>(root).SelectMany(t => t.Inlines.OfType<Hyperlink>());

    private static string LinkText(Hyperlink link) => new TextRange(link.ContentStart, link.ContentEnd).Text;

    // ── Watched by the collector service (#172) ─────────────────────────────

    [Fact]
    public void AServerTheServiceWatches_ShowsItsHistory_ItsOpenAlerts_AndNoStartButton() => UiThread.Run(() =>
    {
        Setup();
        // The service's own history: ten minutes of samples and scores, and a CPU alert still open.
        var servicePath = Path.Combine(_dir, "collector", "history.db");
        var who = new HistoryConnection(_quiet.Id, "Quiet", "quiet", "Sales");
        var now = DateTime.UtcNow;
        var alert = new Engine.Alerts.Alert(Guid.NewGuid(), _quiet.Id, HealthIndicator.Cpu, HealthLevel.Critical, now.AddMinutes(-4), null, 97, 90, "CPU 97%");
        using (var store = new HistoryStore(servicePath))
        {
            store.Open();
            var records = new List<HistoryRecord> { new AlertRecord(who, now.AddMinutes(-4), alert) };
            for (var i = 10; i > 0; i--)
            {
                records.Add(new MetricSampleRecord(who, now.AddMinutes(-i), new LiveMetricSample(DateTime.Now, 1, 2, 3, 4, 5, 6, 97, 100, 7, 1, 2, 30, 40, 5000, 0, 99.5)));
                records.Add(new HealthScoreRecord(who, now.AddMinutes(-i), 72, true));
            }
            store.Write(records);
        }
        _monitoring!.SetWatchedElsewhere(
            new Dictionary<Guid, ElsewhereWatch> { [_quiet.Id] = new(CollectorLink.ShortName, new ScoreHistoryPoint(now.AddMinutes(-1), 72, true), [alert]) },
            new Dictionary<Guid, string> { [_quiet.Id] = "Watched by the SqlVitals Collector service on this PC." },
            [new HistorySource(new HistoryReader(servicePath), CollectorLink.ServiceName)]);

        var page = Open(_quiet);

        Assert.Equal("Watched by the collector", page.BtnMonitor.Content);
        Assert.False(page.BtnMonitor.IsEnabled);
        Assert.Contains("watched by the collector service", page.TxtConnectionLine.Text);
        Assert.Equal("CPU · Critical", Assert.Single(Rows<OverviewAlert>(page.AlertsList)).Title);
        Assert.Equal("72", page.RunScore.Text);
        Assert.StartsWith("Not here: the collector service keeps each sample's score", page.TxtNoCosts.Text);
        // The live charts, from the service's history: the app collects nothing for it.
        Assert.True(page.ChartCpu.Data!.Series[0].Points.Count >= 9);
        Assert.Null(_monitoring.Get(_quiet.Id));
    });

    [Fact]
    public void AServerTheServiceFoundUnreachable_SaysSo_WithoutAnOutageItHasNoSessionFor() => UiThread.Run(() =>
    {
        Setup();
        _monitoring!.SetWatchedElsewhere(
            new Dictionary<Guid, ElsewhereWatch> { [_quiet.Id] = new(CollectorLink.ShortName, new ScoreHistoryPoint(DateTime.UtcNow.AddMinutes(-1), 0, false), []) },
            new Dictionary<Guid, string> { [_quiet.Id] = "Watched by the SqlVitals Collector service on this PC." },
            []);

        var page = Open(_quiet);

        Assert.Equal("UNREACHABLE", page.TxtStatus.Text);
        Assert.Equal(Visibility.Collapsed, page.OutageRow.Visibility);
        Assert.Equal(Visibility.Visible, page.HealthRow.Visibility);
    });

    // ── Healthy ──────────────────────────────────────────────────────────────

    [Fact]
    public void Healthy_ShowsTheFullScore_EveryCheckPassing_AndTheHeadlineNumbers() => UiThread.Run(() =>
    {
        Setup();
        var page = Open(_calm);

        Assert.Equal("HEALTHY", page.TxtStatus.Text);
        Assert.Equal("100", page.RunScore.Text);
        Assert.Equal(Visibility.Collapsed, page.OutageRow.Visibility);
        Assert.Equal(Visibility.Visible, page.HealthRow.Visibility);

        // Nothing costs points; the six checks pass, each with its figure.
        Assert.Empty(Rows<CostRow>(page.CostsList));
        Assert.Equal("Nothing: every check is within its thresholds.", page.TxtNoCosts.Text);
        var passing = Rows<string>(page.PassingList);
        Assert.Equal(6, passing.Count);
        Assert.Contains("Blocking · Nothing blocked", passing);

        // No alerts.
        Assert.Empty(Rows<OverviewAlert>(page.AlertsList));
        Assert.Equal("Active alerts", page.TxtAlertsTitle.Text);
        Assert.StartsWith("None: no check has stayed past its threshold", page.TxtNoAlerts.Text);

        // CPU, batch requests/s, waiting tasks and page life expectancy; CPU coloured by its thresholds.
        var headlines = Rows<HeadlineTile>(page.HeadlinesList);
        Assert.Equal(["CPU", "Batch requests/s", "Waiting tasks", "Page life expectancy"], headlines.Select(h => h.Label));
        Assert.Equal(("12 %", HealthBrushesGreen), (headlines[0].Value, headlines[0].ValueBrush));
        Assert.Equal(["4"], headlines.Where(h => h.Label == "Waiting tasks").Select(h => h.Value));
        Assert.All(headlines, h => Assert.Single(h.Trend));
    });

    private static System.Windows.Media.Brush HealthBrushesGreen => Helpers.HealthBrushes.Green;

    [Fact]
    public void SinceYouLastLooked_CountsTheDeadlocksAndFailedJobsAfterTheLastLook() => UiThread.Run(() =>
    {
        Setup();
        var page = Open(_calm);

        Assert.StartsWith("Since ", page.TxtSinceWhen.Text);
        Assert.EndsWith(" ago.", page.TxtSinceWhen.Text);
        var rows = Rows<SinceRow>(page.SinceList);
        Assert.Equal(["Deadlock", "Failed Agent job", "Regressed queries"], rows.Select(r => r.What));
        // One deadlock an hour ago; the one five hours ago was before the last look.
        Assert.Equal("1", rows[0].Count);
        Assert.Matches(@"^Latest at \d\d:\d\d:\d\d\.$", rows[0].Detail);   // no victim in the report
        // Nightly ETL failed half an hour ago; Weekly checks failed yesterday; Backups succeeded.
        Assert.Equal(("1", "Nightly ETL"), (rows[1].Count, rows[1].Detail));
        // Nothing in the monitoring history to compare.
        Assert.Equal("0", rows[2].Count);

        var opened = new List<(Guid, string)>();
        page.OpenPageRequested += (id, tag) => opened.Add((id, tag));
        foreach (var link in Links(page.SinceList))
            Click(link);
        Assert.Equal([(_calm.Id, "Deadlocks"), (_calm.Id, "AgentJobs"), (_calm.Id, "QueryRegressions")], opened);
    });

    [Fact]
    public void SinceYouLastLooked_TheFirstTime_CoversTheLastDay() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new OverviewPage(_calm.Id, monitoring, settings, SinceLastLooked.Window(null, DateTime.UtcNow));
        UiThread.Wait(page.RefreshAsync());
        UiThread.Wait(page.SinceLoaded);

        Assert.Equal("In the last 24 hours: this is the first time this Overview has been opened.", page.TxtSinceWhen.Text);
        // Both deadlocks are in the last day.
        Assert.Equal("2", Rows<SinceRow>(page.SinceList)[0].Count);
    });

    // ── Needs attention ──────────────────────────────────────────────────────

    [Fact]
    public void NeedsAttention_PutsTheAlertFirst_InPlainWords_WithItsEvidence() => UiThread.Run(() =>
    {
        Setup();
        var page = Open(_hot);
        var opened = new List<Route>();
        page.EvidenceRequested += opened.Add;

        Assert.Equal("CRITICAL", page.TxtStatus.Text);
        Assert.Equal("1 active alert", page.TxtAlertsTitle.Text);
        var alert = Assert.Single(Rows<OverviewAlert>(page.AlertsList));
        Assert.Equal("CPU · Critical", alert.Title);
        Assert.Matches(@"^· since \d\d:\d\d · \d+ s so far$", alert.When);
        Assert.Contains("SQL Server's CPU reached 99", alert.Happening);
        Assert.Equal("Investigate →", alert.EvidenceText);
        Assert.Equal("Open Top CPU queries for this server, filtered to this alert", alert.EvidenceToolTip);

        // Investigate (#145): Resource Queries by CPU, from 15 minutes before the alert started.
        var evidence = UiThread.Find<Button>(page.AlertsList).Single();
        evidence.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var route = Assert.Single(opened);
        Assert.Equal(("ResourceQueries", _hot.Id, FocusKind.TopCpu), (route.Tag, route.ConnectionId, route.Focus!.Filter.Kind));
        Assert.Equal(alert.Alert.StartedUtc - EvidenceRoutes.Margin, route.Focus.FromUtc);
        Assert.Null(route.Focus.ToUtc);
        Assert.Equal(FocusSource.Alert, route.Focus.Origin.Source);
    });

    [Fact]
    public void NeedsAttention_SaysWhatIsCostingPoints_AndLinksToThePageThatExplainsIt() => UiThread.Run(() =>
    {
        Setup();
        var page = Open(_hot);
        var opened = new List<Route>();
        page.EvidenceRequested += opened.Add;

        Assert.Equal("80", page.RunScore.Text);
        var cost = Assert.Single(Rows<CostRow>(page.CostsList));
        Assert.Equal(("CPU", "−20", "Top CPU queries →"), (cost.Name, cost.Points, cost.LinkText));
        Assert.StartsWith("Now 99", cost.Why);
        Assert.EndsWith("all 20 of its points.", cost.Why);
        Assert.Equal(5, Rows<string>(page.PassingList).Count);

        // As the server is now, without a window (#145).
        Click(Assert.Single(Links(page.CostsList)));
        var route = Assert.Single(opened);
        Assert.Equal(("ResourceQueries", _hot.Id, FocusSource.HealthCheck), (route.Tag, route.ConnectionId, route.Focus!.Origin.Source));
        Assert.Null(route.Focus.FromUtc);

        // CPU at 99 %: past critical.
        Assert.Equal(Helpers.HealthBrushes.Red, Rows<HeadlineTile>(page.HeadlinesList)[0].ValueBrush);
    });

    [Fact]
    public void TheCharts_MarkTheAlertsStart_OverTheLiveWindow() => UiThread.Run(() =>
    {
        Setup();
        var page = Open(_hot);

        var cpu = page.ChartCpu.Data!;
        Assert.Equal(100, cpu.Top);
        Assert.Equal(2, Assert.Single(cpu.Series).Points.Count);   // the first sample has no rates
        Assert.True(cpu.ToUtc - cpu.FromUtc >= TimeSpan.FromMinutes(10));
        var marker = Assert.Single(cpu.Markers!);
        Assert.Equal(Assert.Single(page.ChartScore.Data!.Markers!).TimeUtc, marker.TimeUtc);
        Assert.Equal(5, page.ChartWaits.Data!.Series.Count);   // Other left out, as Live charts does
        Assert.Equal(PageKind.Live, page.Kind);
        Assert.Equal("Live", page.TxtChartsTitle.Text);
    });

    [Fact]
    public void LiveCharts_OpensTheFullSizeCharts() => UiThread.Run(() =>
    {
        Setup();
        var page = Open(_hot);
        var opened = new List<(Guid, string)>();
        page.OpenPageRequested += (id, tag) => opened.Add((id, tag));

        Click(Links(page.ChartsCard).Single(l => LinkText(l) == "Full-size live charts →"));

        Assert.Equal([(_hot.Id, "LiveMetrics")], opened);
    });

    // ── Unreachable, not monitored, removed ──────────────────────────────────

    [Fact]
    public void Unreachable_SaysWhy_WhatToCheck_AndOffersRetry() => UiThread.Run(() =>
    {
        Setup();
        var page = Open(_down);
        var madeActive = new List<Guid>();
        page.MakeActiveRequested += madeActive.Add;

        Assert.Equal("UNREACHABLE", page.TxtStatus.Text);
        Assert.Equal(Visibility.Visible, page.OutageRow.Visibility);
        Assert.Equal(Visibility.Collapsed, page.HealthRow.Visibility);
        Assert.Equal("SqlVitals can't reach down", page.TxtOutageTitle.Text);
        Assert.Equal("No server in tests.", page.TxtOutageError.Text);
        Assert.StartsWith("Since ", page.TxtOutageSince.Text);
        Assert.Contains("The SQL Server service on down is running.", (IEnumerable<string>)page.ThingsList.ItemsSource);
        Assert.Equal("No sample has worked since monitoring started.", page.TxtBefore.Text);
        Assert.Equal("None: the server can't be reached, so no check can run.", page.TxtNoAlerts.Text);

        // Nothing is asked of a server that can't be reached.
        Assert.DoesNotContain(_calls, c => c.Server == "down" && c.Method != nameof(IWaitStatsRepository.GetLiveMetricsAsync));

        page.BtnMakeActive.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal([_down.Id], madeActive);
    });

    [Fact]
    public void NotMonitored_SaysSo_AndContactsNoServer() => UiThread.Run(() =>
    {
        Setup();
        var before = _calls.Count;
        var page   = Open(_quiet);

        Assert.Equal("OFF", page.TxtStatus.Text);
        Assert.Equal("None: it isn't monitored, so no check is running.", page.TxtNoAlerts.Text);
        Assert.Equal("Nothing to show: it isn't monitored.", page.TxtNoCosts.Text);
        Assert.All(Rows<HeadlineTile>(page.HeadlinesList), h => Assert.Equal("—", h.Value));
        var rows = Rows<SinceRow>(page.SinceList);
        Assert.StartsWith("Not checked: it isn't monitored.", rows[0].Detail);
        Assert.StartsWith("Not checked: it isn't monitored.", rows[1].Detail);
        Assert.Equal(before, _calls.Count);
    });

    [Fact]
    public void ARemovedConnection_SaysSo() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new OverviewPage(Guid.NewGuid(), monitoring, settings, _look);
        UiThread.Layout(page, 1200, 1000);

        Assert.Equal(Visibility.Visible, page.TxtMissing.Visibility);
        Assert.Equal(Visibility.Collapsed, page.Main.Visibility);
    });

    [Fact]
    public void TheScore_SaysHowMuchOfTheLastDayWasMonitored() => UiThread.Run(() =>
    {
        SeedADayWithANightOff(_calm);
        Setup();

        var page = Open(_calm);

        Assert.Equal("Monitored 14 h of the last 24 h", page.TxtCoverage.Text);
        Assert.Equal(Visibility.Visible, page.TxtCoverage.Visibility);
        Assert.Contains("grey: not monitored", page.TxtChartsNote.Text);
    });

    public void Dispose()
    {
        if (_monitoring is { } monitoring)
            UiThread.Run(monitoring.Dispose);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A healthy server (CPU 12 %, four tasks waiting) with two deadlocks, an hour and five hours
    /// ago, and three Agent jobs: one failed half an hour ago, one yesterday, one that worked.
    /// </summary>
    internal static FakeRepository Calm(string server, ConcurrentQueue<(string, string)> calls) => new()
    {
        Unreachable = true,
        Called      = method => calls.Enqueue((server, method)),
        GetLiveMetricsAsync = () => Task.FromResult(new LiveMetricSnapshot(DateTime.Now, 100, 0, 0, 10, 0, 0, SqlCpuUtilizationPct: 12,
                                                                           PageLifeExpectancy: 5_000, MemoryGrantsPending: 0, BufferCacheHitRatio: 99,
                                                                           0, 0, 0, 0, 0, 0, 0, WaitingTasks: 4,
                                                                           // It reports its log and TempDB, so all six checks pass (#173).
                                                                           LogUsedPct: 5, LogUsedDatabase: "Sales", TempDbUsedPct: 10)),
        GetDeadlockHistoryAsync = () => Task.FromResult(new DeadlockHistory(
            [Deadlock(DateTime.UtcNow.AddHours(-1)), Deadlock(DateTime.UtcNow.AddHours(-5))],
            DeadlockSource.RingBuffer, 2, 0, null, null)),
        GetAgentJobsAsync = () =>
        {
            var now = DateTime.Now;   // the server is on this PC's clock
            return Task.FromResult(new AgentJobList(
            [
                Job("Nightly ETL",   JobOutcome.Failed,    now.AddMinutes(-35)),
                Job("Weekly checks", JobOutcome.Failed,    now.AddDays(-1)),
                Job("Backups",       JobOutcome.Succeeded, now.AddMinutes(-20)),
            ], AgentServiceState.Running, now, null));
        },
    };

    private static DeadlockReport Deadlock(DateTime utc) => new(utc, [], [], "<deadlock/>");

    private static AgentJob Job(string name, JobOutcome outcome, DateTime start) =>
        new(Guid.NewGuid(), name, true, null, null, null, outcome, start, TimeSpan.FromMinutes(5), null,
            1, outcome == JobOutcome.Failed ? 1 : 0, outcome == JobOutcome.Succeeded ? 1 : 0,
            null, null, null, null, "");
}
