using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Monitoring;

/// <summary>
/// The collector without a window (#171), on a fake clock: its interval, a tick skipped while a
/// sample runs, backoff after failures, Pause and Dispose stopping it, and its updates in order.
/// </summary>
public sealed class CollectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private static LiveMetricSnapshot Snapshot(double cpu = 12) =>
        new(DateTime.UtcNow, 100, 0, 0, 10, 0, 0, SqlCpuUtilizationPct: cpu, PageLifeExpectancy: 5_000,
            MemoryGrantsPending: 0, BufferCacheHitRatio: 99, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Live metrics from a function; every call's fake time and token recorded.</summary>
    private sealed class FakeLive(TimeProvider time, Func<CancellationToken, Task<LiveMetricSnapshot>> answer) : ILiveMetricsRepository
    {
        public readonly ConcurrentQueue<(DateTimeOffset At, CancellationToken Token)> Calls = new();

        public Task<LiveMetricSnapshot> GetLiveMetricsAsync(CancellationToken cancellationToken = default)
        {
            Calls.Enqueue((time.GetUtcNow(), cancellationToken));
            return answer(cancellationToken);
        }

        public Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The collector reads live metrics only.");
    }

    private static WaitStatsException Failed(int number, string message) =>
        new("LiveMetricsRepository", "GetLiveMetricsAsync", new InvalidOperationException(message), SqlFailure.Classify(number, message));

    // Moves the clock a tick at a time, giving the loop (on the thread pool) a moment after each.
    private static async Task Run(FakeTimeProvider time, TimeSpan span)
    {
        for (var t = TimeSpan.Zero; t < span; t += Collector.Tick)
        {
            time.Advance(Collector.Tick);
            await Task.Delay(15);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting.");
            await Task.Delay(10);
        }
    }

    // ── Schedule ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AfterASample_TheNextIsDueAtTheInterval()
    {
        var time = new FakeTimeProvider(T0);
        using var collector = new Collector(Guid.NewGuid(), new FakeLive(time, _ => Task.FromResult(Snapshot())), 10, time: time);
        collector.Start();

        await collector.CollectNowAsync();

        Assert.Equal(TimeSpan.FromSeconds(10), collector.TimeUntilNextSample);
        Assert.Equal(HealthLevel.Healthy, collector.State.Health);
        collector.IntervalSeconds = 30;
        Assert.Equal(TimeSpan.FromSeconds(30), collector.TimeUntilNextSample);
    }

    [Fact]
    public async Task Failures_BackOff_AndASampleThatWorks_ResetsIt()
    {
        var time = new FakeTimeProvider(T0);
        var fail = true;
        var live = new FakeLive(time, _ => fail
            ? Task.FromException<LiveMetricSnapshot>(Failed(-2, "Timeout expired. Password=hunter2"))
            : Task.FromResult(Snapshot()));
        using var collector = new Collector(Guid.NewGuid(), live, 10, time: time, redact: text => text.Replace("hunter2", "***"));
        collector.Start();

        for (var failures = 1; failures <= 3; failures++)
        {
            await Assert.ThrowsAsync<WaitStatsException>(collector.CollectNowAsync);
            Assert.Equal(HealthRules.NextDelay(TimeSpan.FromSeconds(10), failures), collector.TimeUntilNextSample);
        }
        var state = collector.State;
        Assert.Equal((HealthLevel.Unavailable, 3), (state.Health, state.ConsecutiveFailures));
        Assert.Equal("Timeout expired. Password=***", state.LastError);
        Assert.Equal(time.GetLocalNow().DateTime, state.FailingSince); // the clock's own local time

        fail = false;
        await collector.CollectNowAsync();
        Assert.Equal(TimeSpan.FromSeconds(10), collector.TimeUntilNextSample);
        Assert.Equal((0, null, null), (collector.State.ConsecutiveFailures, collector.State.LastError, collector.State.FailingSince));
    }

    [Fact]
    public async Task ALockTimeout_SkipsTheSample_WithoutCountingAsAFailure()
    {
        var time = new FakeTimeProvider(T0);
        var answers = new Queue<Task<LiveMetricSnapshot>>(
        [
            Task.FromResult(Snapshot()),
            Task.FromException<LiveMetricSnapshot>(Failed(SqlFailure.LockTimeoutError, "Lock request time out period exceeded.")),
        ]);
        using var collector = new Collector(Guid.NewGuid(), new FakeLive(time, _ => answers.Dequeue()), 10, time: time);

        await collector.CollectNowAsync();
        await collector.CollectNowAsync();

        Assert.Equal((HealthLevel.Healthy, 0), (collector.State.Health, collector.State.ConsecutiveFailures));
        Assert.Contains("sample was skipped", collector.State.HealthText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALockTimeoutAfterDispose_IsntTakenForASkippedSample()
    {
        var time    = new FakeTimeProvider(T0);
        var reading = new TaskCompletionSource<LiveMetricSnapshot>();
        var live    = new FakeLive(time, _ => reading.Task);
        var collector = new Collector(Guid.NewGuid(), live, 10, time: time);
        var sample = collector.CollectNowAsync();
        await WaitUntil(() => !live.Calls.IsEmpty);

        collector.Dispose();
        reading.SetException(Failed(SqlFailure.LockTimeoutError, "Lock request time out period exceeded."));

        await Assert.ThrowsAsync<WaitStatsException>(() => sample);
    }

    [Fact]
    public async Task CollectNow_ReadsOffTheCallersThread()
    {
        var time   = new FakeTimeProvider(T0);
        var caller = Environment.CurrentManagedThreadId;
        var reader = 0;
        var live   = new FakeLive(time, _ => { reader = Environment.CurrentManagedThreadId; return Task.FromResult(Snapshot()); });
        using var collector = new Collector(Guid.NewGuid(), live, 10, time: time);

        await collector.CollectNowAsync();

        Assert.NotEqual(caller, reader);
    }

    // ── The loop ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLoop_CollectsAtTheInterval_FromTheFirstTick()
    {
        var time = new FakeTimeProvider(T0);
        var live = new FakeLive(time, _ => Task.FromResult(Snapshot()));
        using var collector = new Collector(Guid.NewGuid(), live, 10, time: time);
        collector.Start();
        await Task.Delay(50); // the loop starts on the thread pool

        await Run(time, TimeSpan.FromSeconds(32));

        var times = live.Calls.Select(c => c.At).ToList();
        Assert.InRange(times.Count, 3, 4);
        Assert.InRange(times[0] - T0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.All(times.Zip(times.Skip(1)), pair =>
            Assert.InRange(pair.Second - pair.First, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public async Task ATickWhileASampleRuns_IsSkipped_AndCollectNowJoinsIt()
    {
        var time    = new FakeTimeProvider(T0);
        var reading = new TaskCompletionSource<LiveMetricSnapshot>();
        var live    = new FakeLive(time, _ => reading.Task);
        using var collector = new Collector(Guid.NewGuid(), live, 1, time: time);
        collector.Start();
        await Task.Delay(50);

        await Run(time, TimeSpan.FromSeconds(2));
        await WaitUntil(() => !live.Calls.IsEmpty);
        var joined = collector.CollectNowAsync();
        await Run(time, TimeSpan.FromSeconds(20));

        Assert.Single(live.Calls);
        Assert.False(joined.IsCompleted);
        reading.SetResult(Snapshot());
        await joined;
        Assert.Single(live.Calls);
    }

    [Fact]
    public async Task Pause_CancelsTheSampleBeingRead_AndStopsTheLoop()
    {
        var time = new FakeTimeProvider(T0);
        var live = new FakeLive(time, token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => Snapshot(), token));
        using var collector = new Collector(Guid.NewGuid(), live, 1, time: time);
        collector.Start();
        await Task.Delay(50);
        await Run(time, TimeSpan.FromSeconds(2));
        await WaitUntil(() => !live.Calls.IsEmpty);

        collector.Pause();
        await Run(time, TimeSpan.FromSeconds(10));

        var (_, token) = Assert.Single(live.Calls);
        Assert.True(token.IsCancellationRequested);
        Assert.False(collector.State.IsRunning);
        Assert.Null(collector.State.LastError);
        Assert.Equal(TimeSpan.Zero, collector.TimeUntilNextSample);
    }

    [Fact]
    public async Task Dispose_StopsTheLoop()
    {
        var time = new FakeTimeProvider(T0);
        var live = new FakeLive(time, _ => Task.FromResult(Snapshot()));
        var collector = new Collector(Guid.NewGuid(), live, 1, time: time);
        collector.Start();
        await Task.Delay(50);

        collector.Dispose();
        await Run(time, TimeSpan.FromSeconds(5));

        Assert.Empty(live.Calls);
        Assert.Equal(Task.CompletedTask, collector.CollectNowAsync());
    }

    // ── Updates ───────────────────────────────────────────────────────────────

    private sealed class Sink : IHistorySink
    {
        public readonly ConcurrentQueue<HistoryRecord> Records = new();
        public void Enqueue(HistoryRecord record) => Records.Enqueue(record);
    }

    private sealed class NoDetail : IHistoryRepository
    {
        public Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries, CancellationToken cancellationToken = default) =>
            Task.FromException<HistoryDetailSnapshot>(new InvalidOperationException("No details in these tests."));

        public Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QueryTextInfo>>([]);
    }

    [Fact]
    public async Task Updates_ComeInOrder_AndAlertsEndBeforeDisposeReturns()
    {
        var time   = new FakeTimeProvider(T0);
        var sink   = new Sink();
        var id     = Guid.NewGuid();
        var history = new HistoryRecorder(new HistoryConnection(id, "Hot", "hot", ""), new NoDetail(), sink, (_, _) => { }, time);
        var collector = new Collector(id, new FakeLive(time, _ => Task.FromResult(Snapshot(cpu: 99))), 10, history, time)
        {
            AlertSamples = 1,
        };
        var updates = new List<CollectorUpdate>();
        collector.Updated += updates.Add;
        collector.Start();

        await collector.CollectNowAsync();
        collector.Dispose();

        Assert.Equal([CollectorUpdateKind.State, CollectorUpdateKind.Alerts, CollectorUpdateKind.Sample, CollectorUpdateKind.State, CollectorUpdateKind.Alerts],
                     updates.Select(u => u.Kind));
        Assert.Equal(AlertChangeKind.Started, Assert.Single(updates[1].Alerts!).Kind);
        Assert.Single(updates[2].State.ActiveAlerts);
        var ended = Assert.Single(updates[4].Alerts!);
        Assert.Equal((AlertChangeKind.Ended, AlertEndReason.MonitoringStopped), (ended.Kind, ended.Alert.EndReason));
        Assert.Contains(sink.Records, r => r is HealthScoreRecord);
        Assert.Contains(sink.Records, r => r is AlertRecord { Alert.EndReason: AlertEndReason.MonitoringStopped });
    }

    [Fact]
    public async Task AListenerThatThrows_DoesntFailTheCollection()
    {
        var time = new FakeTimeProvider(T0);
        using var collector = new Collector(Guid.NewGuid(), new FakeLive(time, _ => Task.FromResult(Snapshot())), 10, time: time);
        collector.Updated += _ => throw new InvalidOperationException("a listener's bug");

        await collector.CollectNowAsync();

        Assert.Equal(HealthLevel.Healthy, collector.State.Health);
    }

    [Fact]
    public async Task NewThresholds_RegradeTheLastSample_StraightAway()
    {
        var time = new FakeTimeProvider(T0);
        using var collector = new Collector(Guid.NewGuid(), new FakeLive(time, _ => Task.FromResult(Snapshot(cpu: 60))), 10, time: time);
        await collector.CollectNowAsync();
        Assert.Equal(HealthLevel.Healthy, collector.State.Health);

        collector.Thresholds = HealthThresholds.Default with { CpuPct = new HealthThreshold(40, 50) };

        Assert.Equal(HealthLevel.Critical, collector.State.Health);
    }
}
