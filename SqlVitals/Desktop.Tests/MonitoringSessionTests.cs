using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Models;
using Xunit.Abstractions;

namespace SqlVitals.Desktop.Tests;

/// <summary>Real-time tests, run on their own: the timing they check needs a thread pool that isn't busy with every other test.</summary>
[CollectionDefinition(RealTime, DisableParallelization = true)]
public sealed class RealTimeTests
{
    public const string RealTime = "Real time";
}

/// <summary>
/// Monitoring off the UI thread (#171): a page that keeps the window busy no longer delays the
/// samples, which the window catches up on, in order, when it's free again.
/// </summary>
[Collection(RealTimeTests.RealTime)]
public sealed class MonitoringSessionTests(ITestOutputHelper output)
{
    private static LiveMetricSnapshot Snapshot() =>
        new(DateTime.UtcNow, 100, 0, 0, 10, 0, 0, SqlCpuUtilizationPct: 12, PageLifeExpectancy: 5_000,
            MemoryGrantsPending: 0, BufferCacheHitRatio: 99, 0, 0, 0, 0, 0, 0, 0);

    // Runs the dispatcher for a while, as the app's message loop would.
    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [Fact]
    public void ABusyWindow_DoesntDelayTheSamples() => UiThread.Run(() =>
    {
        var clock   = Stopwatch.StartNew();
        var reads   = new ConcurrentQueue<(TimeSpan At, int Thread)>();
        var applied = new List<(TimeSpan At, int Thread)>();
        var ui      = Environment.CurrentManagedThreadId;
        var repo = new FakeRepository
        {
            GetLiveMetricsAsync = () =>
            {
                reads.Enqueue((clock.Elapsed, Environment.CurrentManagedThreadId));
                return Task.FromResult(Snapshot());
            },
        };
        using var session = new MonitoringSession(Guid.NewGuid(), repo, "fp", intervalSeconds: 1);
        session.SampleAdded += (_, _) => applied.Add((clock.Elapsed, Environment.CurrentManagedThreadId));
        session.Start();
        Pump(TimeSpan.FromSeconds(1.5));

        // A heavy page: the UI thread does nothing else for 3.5 seconds.
        var busyFrom = clock.Elapsed;
        Thread.Sleep(TimeSpan.FromSeconds(3.5));
        var busyTo = clock.Elapsed;
        Pump(TimeSpan.FromSeconds(0.5));
        // Stopped, and everything it read applied, before counting.
        session.Pause();
        Pump(TimeSpan.FromSeconds(0.2));

        output.WriteLine($"UI thread busy {Seconds(busyFrom)}–{Seconds(busyTo)}");
        foreach (var (at, thread) in reads)
            output.WriteLine($"sample read at {Seconds(at)} on thread {thread}{(thread == ui ? " (UI)" : "")}");
        foreach (var (at, _) in applied)
            output.WriteLine($"sample shown at {Seconds(at)}");

        var duringBusy = reads.Where(r => r.At > busyFrom && r.At < busyTo).ToList();
        Assert.InRange(duringBusy.Count, 2, 5);                 // about one a second, while the window was busy
        Assert.All(reads, r => Assert.NotEqual(ui, r.Thread));  // never read on the UI thread
        Assert.All(applied, a => Assert.Equal(ui, a.Thread));   // always shown on it
        Assert.Equal(reads.Count, session.Samples.Count);       // and none lost
    });

    // A server at 99 % CPU, so an alert starts on the first sample (AlertSamples = 1).
    private static FakeRepository Hot() => new()
    {
        GetLiveMetricsAsync = () => Task.FromResult(new LiveMetricSnapshot(DateTime.UtcNow, 100, 0, 0, 10, 0, 0, SqlCpuUtilizationPct: 99,
            PageLifeExpectancy: 5_000, MemoryGrantsPending: 0, BufferCacheHitRatio: 99, 0, 0, 0, 0, 0, 0, 0)),
    };

    private static MonitoringSession HotSession(FakeRepository repo, List<string> heard)
    {
        var id = Guid.NewGuid();
        var history = new Engine.History.HistoryRecorder(new Engine.History.HistoryConnection(id, "Hot", "hot", ""), repo,
                                                         new NoSink(), (_, _) => { });
        var session = new MonitoringSession(id, repo, "fp", history: history) { AlertSamples = 1 };
        session.AlertsChanged += (_, changes) => heard.Add($"{changes[0].Kind}");
        return session;
    }

    private sealed class NoSink : Engine.History.IHistorySink
    {
        public void Enqueue(Engine.History.HistoryRecord record) { }
    }

    [Fact]
    public void AfterDispose_ItIsntRunning_EvenWithNoAlertActive() => UiThread.Run(() =>
    {
        var session = new MonitoringSession(Guid.NewGuid(), new FakeRepository { GetLiveMetricsAsync = () => Task.FromResult(Snapshot()) }, "fp");
        session.Start();
        Assert.True(session.IsRunning);

        session.Dispose();

        Assert.False(session.IsRunning);
    });

    [Fact]
    public void DisposedFromItsOwnHandler_ItsAlertEndsAreStillHeard() => UiThread.Run(() =>
    {
        var heard = new List<string>();
        var session = HotSession(Hot(), heard);
        // As MonitoringManager may: something a session says leads to its removal.
        session.StateChanged += s => { if (s.ActiveAlerts.Count > 0) s.Dispose(); };
        session.Start();

        UiThread.Wait(session.CollectNowAsync());

        Assert.Equal(["Started", "Ended"], heard);
    });

    [Fact]
    public void AfterDispose_NoMoreSamplesAreRaised() => UiThread.Run(() =>
    {
        var reading = new TaskCompletionSource<LiveMetricSnapshot>();
        var session = new MonitoringSession(Guid.NewGuid(), new FakeRepository { GetLiveMetricsAsync = () => reading.Task }, "fp");
        var samples = 0;
        session.SampleAdded += (_, _) => samples++;
        _ = session.CollectNowAsync();

        session.Dispose();
        reading.SetResult(Snapshot());
        Pump(TimeSpan.FromSeconds(0.3));

        Assert.Equal(0, samples);
        Assert.Empty(session.Samples);
    });

    private static string Seconds(TimeSpan at) => at.TotalSeconds.ToString("0.00 s", CultureInfo.InvariantCulture);
}
