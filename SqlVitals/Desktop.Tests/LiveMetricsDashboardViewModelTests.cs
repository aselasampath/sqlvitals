using System.Collections.Concurrent;
using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using Chart = SqlVitals.Desktop.ViewModels.Pages.LiveMetricsDashboardViewModel.Chart;
using Metric = SqlVitals.Desktop.ViewModels.Pages.LiveMetricsDashboardViewModel.Metric;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Live charts' view model on its own (#189): no page, no STA thread, the monitoring over fake
/// servers. "Hot" is monitored at 99 % CPU, "Quiet" isn't. It plots the session's samples while
/// shown and not paused, reads a past range and last week from the history, says what Start and
/// Stop would do, reads the process map with each sample while it's shown, and stops following
/// the session once left.
/// </summary>
public sealed class LiveMetricsDashboardViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();
    private readonly FakeShellStatus _shell = new();
    private readonly FakeNavigator _navigator = new();

    private readonly ConnectionSettings _hot   = new() { Server = "hot",   Name = "Hot",   Database = "Sales", Authentication = SqlAuthMode.Windows };
    private readonly ConnectionSettings _quiet = new() { Server = "quiet", Name = "Quiet", Database = "Sales", Authentication = SqlAuthMode.Windows };

    private MonitoringManager? _monitoring;

    private void Setup()
    {
        var settings = new ConnectionSettingsService(_dir);
        settings.Upsert(_hot);
        settings.Upsert(_quiet);
        settings.SetActive(_hot.Id);
        settings.SetStartMonitoringAutomatically(true);
        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                            (conn, _) => conn.Server == "hot" ? Hot() : MonitoringManagerTests.Counting(conn.Server, _calls));
        _monitoring.Sync(settings.Load(), ActivitySources.AutomaticAtStartup);
        Sample();
        Sample();
    }

    private FakeRepository Hot() => MonitoringManagerTests.Hot("hot", _calls);

    // The page's own repository: the active connection's, which the process map reads.
    private readonly List<string> _pageReads = [];

    private void Sample() => UiThread.Wait(_monitoring!.Get(_hot.Id)!.CollectNowAsync());

    private LiveMetricsDashboardViewModel Connected(ConnectionSettings conn, FakeHistoryReader? history = null)
    {
        var repo  = new FakeRepository { Called = _pageReads.Add, GetProcessesAsync = () => Task.FromResult<IEnumerable<ProcessNode>>([]) };
        var model = new LiveMetricsDashboardViewModel(repo, _monitoring, conn.Id,
                                                      history is null ? null : new ConnectionHistory(history, conn.Id), conn.Name, _navigator);
        model.Connect("LiveMetrics", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    private static MetricHistoryPoint Point(DateTime utc, double cpu) =>
        new(utc, TimeSpan.Zero, new LiveMetricSample(utc, 1, 2, 3, 4, 5, 6, cpu, 100, 7, 1, 2, 30, 40, 5000, 0, 99.5));

    // ── Loaded ───────────────────────────────────────────────────────────

    [Fact]
    public void Monitored_PlotsTheSessionsSamples_ItsScore_AndStop() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Connected(_hot);

        UiThread.Wait(model.OnNavigatedToAsync());

        Assert.Contains("Read Load", _shell.Reports);
        Assert.True(model.IsMonitoring);
        Assert.True(model.CanPause);
        Assert.Equal(_monitoring!.Get(_hot.Id)!.Samples.Count, model.Points(Metric.Cpu).Count);
        Assert.Equal(99, model.Points(Metric.Cpu)[^1].Value);
        Assert.Equal(("84", (HealthLevel?)HealthLevel.Critical), (model.HealthScoreText, model.HealthLevel));
        Assert.Equal(("■  Stop monitoring", "Stop monitoring"), (model.MonitorText, model.MonitorName));
        Assert.False(model.ShowsMonitoringOff);
        Assert.NotEqual("--", model.NextSampleIn);
        Assert.Equal("Hot", model.Breakdown.Name);
        Assert.False(model.Breakdown.Paused);
        Assert.Equal("every 10s", model.IntervalNames[model.IntervalIndex]);
    });

    [Fact]
    public void NotMonitored_SaysNothingIsCollected_RefreshSendsNothing_AndStartMonitors() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Connected(_quiet);
        UiThread.Wait(model.OnNavigatedToAsync());
        UiThread.Wait(model.RefreshAsync());

        Assert.False(model.IsMonitoring);
        Assert.False(model.CanPause);
        Assert.True(model.ShowsMonitoringOff);
        Assert.Equal("--", model.NextSampleIn);
        Assert.Equal("—", model.HealthScoreText);
        Assert.Null(model.HealthLevel);
        Assert.True(model.Breakdown.Paused);
        Assert.Empty(model.Points(Metric.Cpu));
        Assert.DoesNotContain(_calls, c => c.Server == "quiet");

        model.StartMonitoringCommand.Execute(null);
        UiThread.WaitUntil(() => model.IsMonitoring);
        Assert.Equal("■  Stop monitoring", model.MonitorText);
        Assert.False(model.ShowsMonitoringOff);

        model.ToggleMonitoringCommand.Execute(null);
        Assert.False(_monitoring!.IsMonitoring(_quiet.Id));
        Assert.True(model.ShowsMonitoringOff);
    });

    // ── While shown, paused, and after ───────────────────────────────────

    [Fact]
    public void ASample_IsPlotted_AndTheMapRead_UnlessPaused_AndNotOnceLeft() => OnDispatcher.Run(() =>
    {
        Setup();
        var model   = Connected(_hot);
        var sampled = 0;
        model.Sampled += () => sampled++;
        UiThread.Wait(model.OnNavigatedToAsync());
        var plotted = model.Points(Metric.Cpu).Count;
        _shell.Reports.Clear();

        Sample();
        UiThread.Wait(model.MapRead);
        Assert.Equal(plotted + 1, model.Points(Metric.Cpu).Count);
        Assert.Contains("Read Timed", _shell.Reports);
        Assert.NotNull(model.Processes);
        Assert.True(sampled > 0);

        // Paused: the charts stand still while the session collects; Resume plots what came.
        model.SetPaused(true);
        Sample();
        Assert.Equal(plotted + 1, model.Points(Metric.Cpu).Count);
        model.SetPaused(false);
        Assert.Equal(plotted + 2, model.Points(Metric.Cpu).Count);

        // The map isn't read while it is closed.
        model.CloseCommand.Execute(Chart.ProcessMap);
        Assert.False(model.ShowsProcessMap);
        var reads = _pageReads.Count;
        Sample();
        Assert.Equal(reads, _pageReads.Count);

        model.OnNavigatedFrom();
        _shell.Reports.Clear();
        Sample();
        Assert.Equal(plotted + 3, model.Points(Metric.Cpu).Count);
        Assert.Empty(_shell.Reports);
    });

    // ── A past range, and last week ──────────────────────────────────────

    [Fact]
    public void APastRange_PlotsTheHistory_OnItsWholeAxis_AndLiveAgainTheSamples() => OnDispatcher.Run(() =>
    {
        Setup();
        var now     = DateTime.UtcNow;
        var history = new FakeHistoryReader { Metrics = [Point(now.AddMinutes(-40), 50), Point(now.AddMinutes(-20), 60)] };
        var model   = Connected(_hot, history);
        UiThread.Wait(model.OnNavigatedToAsync());

        UiThread.Wait(model.PickRangeAsync(HistoryRange.LastHour));

        Assert.False(model.IsLive);
        Assert.False(model.CanPause);
        Assert.Equal([50.0, 60.0], model.Points(Metric.Cpu).Where(p => p.Value is not null).Select(p => p.Value!.Value));
        Assert.NotNull(model.Axis.MinTicks);
        Assert.Equal("HH:mm", model.Axis.Format);
        Assert.NotEqual(string.Empty, model.HistoryStatus);
        Assert.NotNull(model.DataTo);

        UiThread.Wait(model.PickRangeAsync(HistoryRange.Live));
        Assert.True(model.IsLive);
        Assert.Null(model.Axis.MinTicks);
        Assert.Equal(99, model.Points(Metric.Cpu)[^1].Value);
        Assert.Equal(string.Empty, model.HistoryStatus);
    });

    [Fact]
    public void AHistoryThatCantBeRead_SaysSo_OnTheLineAndTheTopBar() => OnDispatcher.Run(() =>
    {
        Setup();
        var history = new FakeHistoryReader { MetricsFail = new IOException("The history file is locked.") };
        var model   = Connected(_hot, history);
        UiThread.Wait(model.OnNavigatedToAsync());

        UiThread.Wait(model.PickRangeAsync(HistoryRange.Last24Hours));

        Assert.Equal("Could not read the history: The history file is locked.", model.HistoryStatus);
        Assert.Contains("Error Timed: The history file is locked.", _shell.Reports);
    });

    [Fact]
    public void CompareToBaseline_DrawsLastWeek_AndTheWaitsTotalOfTheCategoriesShown() => OnDispatcher.Run(() =>
    {
        Setup();
        var now     = DateTime.UtcNow;
        var history = new FakeHistoryReader { Metrics = [Point(now.AddDays(-7).AddMinutes(-40), 40), Point(now.AddDays(-7).AddMinutes(-20), 45),
                                                         Point(now.AddMinutes(-40), 50), Point(now.AddMinutes(-20), 60)] };
        var model   = Connected(_hot, history);
        UiThread.Wait(model.OnNavigatedToAsync());
        UiThread.Wait(model.PickRangeAsync(HistoryRange.LastHour));

        model.CompareToBaseline = true;
        UiThread.Wait(model.BaselineLoaded);

        Assert.Equal([40.0, 45.0], model.Baseline(Metric.Cpu).Where(p => p.Value is not null).Select(p => p.Value!.Value));
        // CPU, I/O, Locks, Mem and Net: 1 + 2 + 3 + 4 + 5; Other is off.
        Assert.All(model.BaselineWaits.Where(p => p.Value is not null), p => Assert.Equal(15, p.Value));
        Assert.Contains("last week", model.HistoryStatus, StringComparison.OrdinalIgnoreCase);

        model.WaitsShown[5].IsShown = true;
        Assert.All(model.BaselineWaits.Where(p => p.Value is not null), p => Assert.Equal(21, p.Value));

        model.CompareToBaseline = false;
        Assert.Empty(model.Baseline(Metric.Cpu));
    });

    // ── An alert's window (#145) ─────────────────────────────────────────

    [Fact]
    public void AnAlertsWindow_IsReadFirst_UntilShowLive() => OnDispatcher.Run(() =>
    {
        Setup();
        var now     = DateTime.UtcNow;
        var history = new FakeHistoryReader { Metrics = [Point(now.AddMinutes(-30), 70)] };
        var model   = Connected(_hot, history);
        var focus   = new PageFocus(new FocusFilter(FocusKind.TopCpu), new FocusOrigin(FocusSource.Alert, "CPU · critical"),
                                    now.AddMinutes(-45), now.AddMinutes(-15));

        var range = model.FocusRange(focus);
        Assert.NotNull(range);
        model.ShowFocus(range!);
        UiThread.Wait(model.OnNavigatedToAsync());

        Assert.False(model.IsLive);
        Assert.True(model.ShowsWindow);
        Assert.StartsWith("showing ", model.FocusNote);
        Assert.Equal("Show live", model.ShowAllText);
        Assert.Equal([70.0], model.Points(Metric.Cpu).Where(p => p.Value is not null).Select(p => p.Value!.Value));

        Assert.True(model.ClearFocus());
        Assert.False(model.ClearFocus());
        Assert.Null(model.FocusNote);
        Assert.Null(model.FocusRange(focus with { ToUtc = focus.FromUtc }));
    });

    // ── Its controls ─────────────────────────────────────────────────────

    [Fact]
    public void TheIntervalPicked_IsTheConnectionsInTheBackgroundToo_AndTheSettingsLinkOpensSettings() => OnDispatcher.Run(() =>
    {
        Setup();
        var model = Connected(_hot);
        UiThread.Wait(model.OnNavigatedToAsync());

        model.IntervalIndex = model.IntervalNames.ToList().IndexOf("every 30s");
        Assert.Equal(30, _monitoring!.IntervalFor(_hot.Id));

        UiThread.Wait(model.OpenMonitoringSettingsCommand.ExecuteAsync(null));
        Assert.Equal(["Settings"], _navigator.Moves);

        foreach (var chart in Enum.GetValues<Chart>())
            model.CloseCommand.Execute(chart);
        Assert.All(Enum.GetValues<Chart>(), chart => Assert.False(model.Shows(chart)));
    });

    public void Dispose()
    {
        _monitoring?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
