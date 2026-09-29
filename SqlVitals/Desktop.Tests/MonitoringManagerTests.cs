using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// What MonitoringManager sends to a server (#115): with Start monitoring automatically off,
/// nothing, however the app starts or switches; with Start, exactly the connection started.
/// </summary>
public sealed class MonitoringManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    // Every repository call any session made, by connection and method name.
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();

    private string HistoryPath => Path.Combine(_dir, "history.db");

    private MonitoringManager Create() =>
        new(new HistoryWriter(HistoryPath, (_, _) => { }),
            (conn, _) => CountingRepository.Create(conn.Server, _calls));

    private static ConnectionStore Store(bool automatic, params ConnectionSettings[] connections) => new()
    {
        StartMonitoringAutomatically = automatic,
        Connections                  = connections.ToList(),
        ActiveConnectionId           = connections.FirstOrDefault()?.Id,
    };

    private static ConnectionSettings Conn(string server, bool background = false) => new()
    {
        Server              = server,
        Authentication      = SqlAuthMode.Windows,
        MonitorInBackground = background,
    };

    private IEnumerable<string> CallsTo(string method) => _calls.Where(c => c.Method == method).Select(c => c.Server);

    // Runs the dispatcher long enough for the sessions' 1-second timers to fire.
    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Background, (_, _) => frame.Continue = false,
                                        Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [Fact]
    public void Off_StartingAndSwitching_SendNothingToAnyServer()
    {
        var a = Conn("sqlA", background: true);
        var b = Conn("sqlB", background: true);
        var store = Store(false, a, b);

        using (var monitoring = Create())
        {
            monitoring.Sync(store, ActivitySources.AutomaticAtStartup);
            store.ActiveConnectionId = b.Id;                          // switch to B
            monitoring.Sync(store, ActivitySources.ConnectionSelector);
            Pump(TimeSpan.FromSeconds(2.5));

            Assert.Equal(0, monitoring.MonitoredCount);
            Assert.Null(monitoring.Get(a.Id));
            Assert.Null(monitoring.Get(b.Id));
        }

        Assert.Empty(CallsTo(nameof(IWaitStatsRepository.GetLiveMetricsAsync)));
        Assert.Empty(CallsTo(nameof(IWaitStatsRepository.GetHistoryDetailAsync)));
        Assert.Empty(CallsTo(nameof(IWaitStatsRepository.GetProcessesAsync)));
        Assert.Empty(_calls);
    }

    [Fact]
    public void Off_ThenStartOnABackgroundConnection_MonitorsOnlyThatOne()
    {
        var active     = Conn("sqlActive");
        var background = Conn("sqlBackground", background: true);

        using (var monitoring = Create())
        {
            monitoring.Sync(Store(false, active, background), ActivitySources.AutomaticAtStartup);
            monitoring.Start(background.Id, ActivitySources.ConnectionSelector);
            Pump(TimeSpan.FromSeconds(2.5));

            Assert.True(monitoring.IsMonitoring(background.Id));
            Assert.False(monitoring.IsMonitoring(active.Id));
        }

        Assert.Contains("sqlBackground", CallsTo(nameof(IWaitStatsRepository.GetLiveMetricsAsync)));
        Assert.DoesNotContain("sqlActive", _calls.Select(c => c.Server));
    }

    [Fact]
    public void On_MonitorsTheActiveAndBackgroundOnes_AndStopEndsIt()
    {
        var active     = Conn("sqlActive");
        var background = Conn("sqlBackground", background: true);
        var other      = Conn("sqlOther");

        using var monitoring = Create();
        monitoring.Sync(Store(true, active, background, other), ActivitySources.AutomaticAtStartup);

        Assert.Equal(2, monitoring.MonitoredCount);
        Assert.False(monitoring.IsMonitoring(other.Id));

        monitoring.Stop(active.Id, ActivitySources.LiveMetrics);

        Assert.False(monitoring.IsMonitoring(active.Id));
        Assert.StartsWith("Stopped by you at", monitoring.NotMonitoredReason(active.Id));
        Assert.Equal(1, monitoring.MonitoredCount);
    }

    [Fact]
    public void TurningTheSettingOff_StopsEverything_AndOnStartsThemAgain()
    {
        var active = Conn("sqlActive");
        var other  = Conn("sqlOther");
        var store  = Store(true, active, other);

        using var monitoring = Create();
        monitoring.Sync(store, ActivitySources.AutomaticAtStartup);
        monitoring.Start(other.Id, ActivitySources.ConnectionSelector);
        Assert.Equal(2, monitoring.MonitoredCount);

        store.StartMonitoringAutomatically = false;
        monitoring.Sync(store, ActivitySources.Settings);
        Assert.Equal(0, monitoring.MonitoredCount);   // the one started by hand too

        store.StartMonitoringAutomatically = true;
        monitoring.Sync(store, ActivitySources.Settings);
        Assert.True(monitoring.IsMonitoring(active.Id));
        Assert.False(monitoring.IsMonitoring(other.Id));
    }

    [Fact]
    public void StartAllAndStopAll_ActOnTheActiveAndBackgroundConnections()
    {
        var active     = Conn("sqlActive");
        var background = Conn("sqlBackground", background: true);
        var other      = Conn("sqlOther");

        using var monitoring = Create();
        monitoring.Sync(Store(false, active, background, other), ActivitySources.AutomaticAtStartup);

        monitoring.StartAll(ActivitySources.TrayMenu);
        Assert.True(monitoring.IsMonitoring(active.Id));
        Assert.True(monitoring.IsMonitoring(background.Id));
        Assert.False(monitoring.IsMonitoring(other.Id));

        monitoring.StopAll(ActivitySources.TrayMenu);
        Assert.Equal(0, monitoring.MonitoredCount);
    }

    [Fact]
    public void AConnectionWithoutItsPassword_CantBeStarted()
    {
        var conn = new ConnectionSettings { Server = "sqlA", UserName = "monitor" };   // SQL login, no password

        using var monitoring = Create();
        monitoring.Sync(Store(false, conn), ActivitySources.AutomaticAtStartup);
        monitoring.Start(conn.Id, ActivitySources.ConnectionSelector);

        Assert.False(monitoring.CanStart(conn.Id));
        Assert.False(monitoring.IsMonitoring(conn.Id));
        Assert.Contains("password isn't saved", monitoring.NotMonitoredReason(conn.Id));
    }

    [Fact]
    public void StartsAndStops_AreLoggedWithTheirSource_BetweenAppStartedAndAppExited()
    {
        var active = Conn("sqlActive");
        active.Name = "Prod";

        using (var monitoring = Create())
        {
            monitoring.Sync(Store(false, active), ActivitySources.AutomaticAtStartup);
            monitoring.Start(active.Id, ActivitySources.LiveMetrics);
            monitoring.Stop(active.Id, ActivitySources.ConnectionSelector);
            monitoring.Start(active.Id, ActivitySources.TrayMenu);
        }

        var rows = new HistoryReader(HistoryPath).ReadActivity(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1))
                                                 .Reverse().Select(r => r.Entry).ToList();

        Assert.Equal(
            new[]
            {
                (ActivityEvents.AppStarted,        ActivitySources.AutomaticAtStartup),
                (ActivityEvents.MonitoringStarted, ActivitySources.LiveMetrics),
                (ActivityEvents.MonitoringStopped, ActivitySources.ConnectionSelector),
                (ActivityEvents.MonitoringStarted, ActivitySources.TrayMenu),
                (ActivityEvents.MonitoringStopped, ActivitySources.AppExit),
                (ActivityEvents.AppExited,         ActivitySources.AppExit),
            },
            rows.Select(r => (r.Event, r.Source)));
        Assert.All(rows.Where(r => r.Event.StartsWith("Monitoring")), r => Assert.Equal(("Prod", (Guid?)active.Id), (r.ConnectionName, r.ConnectionId)));
        Assert.All(rows, r => Assert.Equal(Environment.MachineName, r.Machine));
        Assert.All(rows, r => Assert.EndsWith(Environment.UserName, r.WindowsUser));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>An IWaitStatsRepository that records every call and fails it, as an unreachable server would.</summary>
    public class CountingRepository : DispatchProxy
    {
        private string _server = "";
        private ConcurrentQueue<(string, string)> _calls = new();

        public static IWaitStatsRepository Create(string server, ConcurrentQueue<(string, string)> calls)
        {
            var proxy = DispatchProxy.Create<IWaitStatsRepository, CountingRepository>();
            var self  = (CountingRepository)(object)proxy;
            self._server = server;
            self._calls  = calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            _calls.Enqueue((_server, method!.Name));
            var type = method.ReturnType;
            if (type == typeof(Task))
                return Task.FromException(new InvalidOperationException("No server in tests."));
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                                   .MakeGenericMethod(type.GetGenericArguments()[0])
                                   .Invoke(null, [new InvalidOperationException("No server in tests.")]);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
