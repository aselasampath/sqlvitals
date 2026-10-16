using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using static SqlVitals.Desktop.ViewModels.Pages.OverviewViewModel;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// A server's Overview's view model on its own (#189): no page, no STA thread, the monitoring over
/// fake servers. "Calm" is healthy with a deadlock and a failed job since the last look, "Hot" the
/// active one at 99 % CPU, "Down" can't be reached, "Quiet" isn't monitored. It says what the page
/// shows by level and status, follows the monitoring only while shown, keeps a list that didn't
/// change, and its buttons and links do what the page's did.
/// </summary>
public sealed class OverviewViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();
    private readonly FakeShellStatus _shell = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly LookWindow _look = SinceLastLooked.Window(DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);

    private readonly ConnectionSettings _calm  = Conn("calm",  "Calm", background: true);
    private readonly ConnectionSettings _hot   = Conn("hot",   "Hot");
    private readonly ConnectionSettings _down  = Conn("down",  "Down", background: true);
    private readonly ConnectionSettings _quiet = Conn("quiet", "Quiet");

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
        "calm" => OverviewPageTests.Calm(conn.Server, _calls),
        _      => MonitoringManagerTests.Counting(conn.Server, _calls),
    };

    // The four saved, Hot active, monitoring started automatically, and a sample from each monitored one.
    private void Setup()
    {
        _settings = new ConnectionSettingsService(_dir);
        foreach (var conn in new[] { _calm, _hot, _down, _quiet })
            _settings.Upsert(conn);
        _settings.SetActive(_hot.Id);
        _settings.SetStartMonitoringAutomatically(true);

        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }), (conn, _) => Repository(conn));
        _monitoring.Sync(_settings.Load(), ActivitySources.AutomaticAtStartup);
        Sample(_hot);
        Sample(_calm);
        Sample(_down);
    }

    private void Sample(ConnectionSettings conn)
    {
        if (_monitoring!.Get(conn.Id) is not { } session)
            return;
        try { UiThread.Wait(session.CollectNowAsync()); }
        catch (InvalidOperationException) { /* "No server in tests.": Down can't be reached */ }
        if (conn != _down)
            MonitoringManagerTests.WaitForServerChecks(session);
    }

    private OverviewViewModel Connected(Guid id, Func<string, Task<string?>>? test = null)
    {
        var model = new OverviewViewModel(id, _monitoring!, _settings!, _look, new DirectWatchStarter(_monitoring!), new FakeUiDispatcher(),
                                          _clipboard, test);
        model.Connect("Overview", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    private OverviewViewModel Opened(ConnectionSettings conn, Func<string, Task<string?>>? test = null)
    {
        var model = Connected(conn.Id, test);
        UiThread.Wait(model.OnNavigatedToAsync());
        UiThread.Wait(model.SinceLoaded);
        return model;
    }

    // ── Loaded ───────────────────────────────────────────────────────────

    [Fact]
    public void Healthy_SaysItsScore_ItsChecks_TheHeadlines_AndWhatHappenedSinceTheLastLook() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Opened(_calm);

        Assert.Contains("Read Load", _shell.Reports);
        Assert.Equal(("Calm", TileStatus.Healthy, "HEALTHY", "100"), (model.Name, model.ServerStatus, model.StatusLabel, model.ScoreText));
        Assert.True(model.ShowsHealth);
        Assert.False(model.ShowsOutage);
        Assert.True(model.ShowsMakeActive);   // Hot is the active one
        Assert.False(model.ShowsActive);
        Assert.Empty(model.Costs);
        Assert.Equal("Nothing: every check is within its thresholds.", model.NoCostsText);
        Assert.Equal(9, model.Passing.Count);
        Assert.Empty(model.AlertsShown);
        Assert.Null(model.WorstSeverity);
        Assert.True(model.ShowsNoAlerts);
        Assert.Equal(("CPU", "12 %", (HealthLevel?)HealthLevel.Healthy), (model.Headlines[0].Label, model.Headlines[0].Value, model.Headlines[0].Level));
        Assert.Null(model.Headlines.Single(h => h.Label == "Waiting tasks").Level);   // no threshold

        Assert.Equal(["Deadlock", "Failed Agent job", "Regressed queries"], model.SinceRows.Select(r => r.What));
        Assert.Equal(("1", (HealthLevel?)HealthLevel.Critical), (model.SinceRows[0].Count, model.SinceRows[0].Level));
        Assert.Equal(("0", (HealthLevel?)HealthLevel.Healthy), (model.SinceRows[2].Count, model.SinceRows[2].Level));
        Assert.Equal("Live", model.ChartsTitle);
        Assert.True(model.IsLive);
        Assert.StartsWith("Health score: lowest ", model.ScoreChartName);
    });

    [Fact]
    public void NeedsAttention_ListsTheAlert_AndWhatCostsPoints_ByLevel() => OnDispatcher.Run(() =>
    {
        Setup();
        Sample(_hot);
        Sample(_hot);
        var model = Opened(_hot);

        Assert.Equal((TileStatus.Critical, "84"), (model.ServerStatus, model.ScoreText));
        Assert.True(model.ShowsActive);
        var alert = Assert.Single(model.AlertsShown);
        Assert.Equal(("CPU · Critical", HealthLevel.Critical), (alert.Title, alert.Severity));
        Assert.Equal(HealthLevel.Critical, model.WorstSeverity);
        Assert.Equal("1 active alert", model.AlertsTitle);
        var cost = Assert.Single(model.Costs);
        Assert.Equal(("CPU", "−16", HealthLevel.Critical), (cost.Name, cost.Points, cost.Level));
        var marker = Assert.Single(model.Charts.Markers);
        Assert.Equal(HealthLevel.Critical, marker.Severity);
        Assert.Contains("Dashed lines: alerts starting.", model.ChartsNote);
    });

    [Fact]
    public void Unreachable_SaysWhy_WhatToCheck_RetriesTests_AndCopiesTheError() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Opened(_down, test: _ => Task.FromResult<string?>("Login failed for user 'sa'."));

        Assert.True(model.ShowsOutage);
        Assert.False(model.ShowsHealth);
        Assert.Equal("SqlVitals can't reach down", model.OutageTitle);
        Assert.Equal("No server in tests.", model.OutageError);
        Assert.Contains("The SQL Server service on down is running.", model.ThingsToCheck);
        Assert.Equal("No sample has worked since monitoring started.", model.BeforeText);
        Assert.StartsWith("Since ", model.OutageSince);
        Assert.Null(model.OutageAction);

        UiThread.Wait(model.RetryCommand.ExecuteAsync(null));
        Assert.StartsWith("Still can't reach it at ", model.OutageAction);

        UiThread.Wait(model.TestCommand.ExecuteAsync(null));
        Assert.Equal("Connection failed: Login failed for user 'sa'.", model.OutageAction);

        model.CopyErrorCommand.Execute(null);
        Assert.Equal("No server in tests.", _clipboard.Text);

        // A test that throws is said on the top bar, not left to the dispatcher.
        var broken = Opened(_down, test: _ => Task.FromException<string?>(new InvalidOperationException("The driver is missing.")));
        UiThread.Wait(broken.TestCommand.ExecuteAsync(null));
        Assert.Contains("Error Refresh: The driver is missing.", _shell.Reports);
        // Nothing but samples is asked of a server that can't be reached.
        Assert.DoesNotContain(_calls, c => c.Server == "down" && c.Method != nameof(IWaitStatsRepository.GetLiveMetricsAsync));
    });

    [Fact]
    public void NotMonitored_SaysSo_AndStartMonitorsIt() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Opened(_quiet);

        Assert.Equal(TileStatus.Off, model.ServerStatus);
        Assert.True(model.IsStopped);
        Assert.Equal("Start monitoring", model.MonitorText);
        Assert.Equal("Nothing to show: it isn't monitored.", model.NoCostsText);
        Assert.All(model.Headlines, h => Assert.Equal("—", h.Value));
        Assert.StartsWith("Not checked: it isn't monitored.", model.SinceRows[0].Detail);
        Assert.Null(model.SinceRows[0].Level);

        UiThread.Wait(model.MonitorCommand.ExecuteAsync(null));
        Assert.True(_monitoring!.IsMonitoring(_quiet.Id));
        Assert.Equal("Pause monitoring", model.MonitorText);
    });

    [Fact]
    public void ARemovedConnection_SaysSo() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Connected(Guid.NewGuid());

        UiThread.Wait(model.OnNavigatedToAsync());

        Assert.True(model.IsMissing);
        Assert.False(model.ShowsServer);
        Assert.False(model.ShowsMakeActive);
        Assert.Equal("Removed connection", model.Name);
    });

    [Fact]
    public void AHistoryFromANewerSqlVitals_SaysOnlyWhatWasCollectedSinceIsShown() => OnDispatcher.Run(() =>
    {
        Directory.CreateDirectory(_dir);
        using (var file = new SqliteConnection($"Data Source={Path.Combine(_dir, "history.db")};Pooling=False"))
        {
            file.Open();
            using var command = file.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {HistoryStore.SchemaVersion + 1};";
            command.ExecuteNonQuery();
        }
        Setup();

        var model = Opened(_calm);

        Assert.StartsWith("The history file is from a newer SqlVitals", model.ChartsNote);
        Assert.StartsWith("The history file is from a newer SqlVitals", model.NoAlertsText);
        Assert.StartsWith("Not checked: The history file", model.SinceRows[2].Detail);
        Assert.DoesNotContain(_shell.Reports, r => r.StartsWith("Error", StringComparison.Ordinal));
    });

    // ── While shown, and after ───────────────────────────────────────────

    [Fact]
    public void ASample_MovesThePage_KeepsAListThatDidntChange_OnlyWhileShown() => OnDispatcher.Run(() =>
    {
        Setup();
        var model    = Opened(_calm);
        var passing  = model.Passing;
        var charts   = model.Charts;
        _shell.Reports.Clear();

        Sample(_calm);
        Assert.Contains("Read Timed", _shell.Reports);
        Assert.Same(passing, model.Passing);   // the same checks pass: the list isn't replaced
        Assert.NotSame(charts, model.Charts);
        Assert.Equal(2, _monitoring!.Get(_calm.Id)!.Samples.Count);

        model.OnNavigatedFrom();
        charts = model.Charts;
        _shell.Reports.Clear();
        Sample(_calm);
        Assert.Empty(_shell.Reports);
        Assert.Same(charts, model.Charts);
    });

    [Fact]
    public void Leaving_WhileTheHistoryIsRead_SaysNothing() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Connected(_calm.Id);

        var opening = model.OnNavigatedToAsync();
        model.OnNavigatedFrom();
        UiThread.Wait(opening);

        Assert.DoesNotContain(_shell.Reports, r => r.StartsWith("Read", StringComparison.Ordinal) || r.StartsWith("Error", StringComparison.Ordinal));
        Assert.False(model.IsBusy);
    });

    [Fact]
    public void APastRange_ChartsTheHistory_AndLiveAgain_TheSamples() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Opened(_calm);

        UiThread.Wait(model.PickRangeAsync(HistoryRange.LastHour));
        Assert.False(model.IsLive);
        Assert.NotEqual("Live", model.ChartsTitle);
        Assert.True(model.Charts.ToUtc - model.Charts.FromUtc >= TimeSpan.FromHours(1));
        Assert.Null(model.LiveInterval);

        UiThread.Wait(model.PickRangeAsync(HistoryRange.Live));
        Assert.True(model.IsLive);
        Assert.Equal("Live", model.ChartsTitle);
        Assert.Null(model.DataTo);
        Assert.NotNull(model.LiveInterval);
    });

    // ── Its links ────────────────────────────────────────────────────────

    [Fact]
    public void TheLinks_AskTheWindowForTheirPages() => OnDispatcher.Run(() =>
    {
        Setup();
        Sample(_hot);
        Sample(_hot);
        var model  = Opened(_hot);
        var asked  = new List<string>();
        var routes = new List<Route>();
        model.OpenPageRequested   += (id, tag) => asked.Add($"{tag} {id == _hot.Id}");
        model.EditRequested       += _ => asked.Add("Edit");
        model.ThresholdsRequested += _ => asked.Add("Thresholds");
        model.AlertsRequested     += _ => asked.Add("Alerts");
        model.MakeActiveRequested += _ => asked.Add("MakeActive");
        model.EvidenceRequested   += routes.Add;

        model.EditCommand.Execute(null);
        model.ChangeThresholdsCommand.Execute(null);
        model.AllAlertsCommand.Execute(null);
        model.MakeActiveCommand.Execute(null);
        model.LiveChartsCommand.Execute(null);
        model.SinceLinkCommand.Execute(model.SinceRows[1]);
        Assert.Equal(["Edit", "Thresholds", "Alerts", "MakeActive", "LiveMetrics True", "AgentJobs True"], asked);

        model.EvidenceCommand.Execute(model.AlertsShown[0]);
        model.CostLinkCommand.Execute(model.Costs[0]);
        model.BlockingChainCommand.Execute(null);
        Assert.Equal(["ResourceQueries", "ResourceQueries", "Processes"], routes.Select(r => r.Tag));
        Assert.Equal(FocusSource.Alert, routes[0].Focus!.Origin.Source);
        Assert.Equal(FocusSource.HealthCheck, routes[1].Focus!.Origin.Source);
    });

    public void Dispose()
    {
        _monitoring?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
