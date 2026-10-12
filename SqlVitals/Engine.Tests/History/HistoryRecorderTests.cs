using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using static SqlVitals.Engine.Tests.History.HistoryStoreTests;

namespace SqlVitals.Engine.Tests.History;

public class HistoryRecorderTests
{
    private static readonly DateTime ServerStart = new(2026, 9, 1, 6, 0, 0);
    private static readonly DateTime ServerT0    = new(2026, 9, 23, 9, 0, 0);

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(HistorySettings.DefaultIntervalMinutes);

    private readonly ManualTime       _time   = new(new DateTimeOffset(T0));
    private readonly FakeRepository   _repo   = new();
    private readonly ListSink         _sink   = new();
    private readonly List<string>     _errors = new();

    private HistoryRecorder Create() => new(Conn, _repo, _sink, (message, _) => _errors.Add(message), _time);

    private static HistoryDetailSnapshot Snap(DateTime serverTime, params QueryTotals[] queries) =>
        new(serverTime, ServerStart, [], [], queries, null);

    private static QueryTotals Query(string hash, long execs, long cpuUs) =>
        new(hash, execs, cpuUs, cpuUs, 0, 0, ServerStart);

    [Fact]
    public void RecordSample_QueuesItWithThisPcsUtcTime()
    {
        var sample = Sample(33);

        Create().RecordSample(sample);

        var record = Assert.IsType<MetricSampleRecord>(Assert.Single(_sink.Records));
        Assert.Equal((Conn, T0, sample), (record.Connection, record.CapturedUtc, record.Sample));
    }

    [Fact]
    public void RecordAlerts_QueuesEachAlertAsItNowStands()
    {
        var started = AlertAt(T0).Alert;
        var ended   = AlertAt(T0.AddMinutes(-5), T0).Alert;

        Create().RecordAlerts([new AlertChange(started, AlertChangeKind.Started), new AlertChange(ended, AlertChangeKind.Ended)]);

        var records = _sink.Records.Cast<AlertRecord>().ToList();
        Assert.Equal(new[] { started, ended }, records.Select(r => r.Alert));
        Assert.All(records, r => Assert.Equal((Conn, T0), (r.Connection, r.CapturedUtc)));
    }

    [Fact]
    public void RecordScore_QueuesTheScoreWithThisPcsUtcTime_AndWhetherTheServerCouldBeReached()
    {
        var recorder = Create();

        recorder.RecordScore(new HealthScore(70, HealthLevel.Critical, [], T0));
        recorder.RecordScore(HealthScore.Unavailable("Login failed.", T0));

        var records = _sink.Records.Cast<HealthScoreRecord>().ToList();
        Assert.Equal(new[] { (70, true), (0, false) }, records.Select(r => (r.Score, r.Reachable)));
        Assert.All(records, r => Assert.Equal((Conn, T0), (r.Connection, r.CapturedUtc)));
    }

    [Fact]
    public void RecordScore_SwallowsAFailingSink()
    {
        var recorder = new HistoryRecorder(Conn, _repo, new ThrowingSink(), (m, _) => _errors.Add(m), _time);

        recorder.RecordScore(new HealthScore(100, HealthLevel.Healthy, [], T0));

        Assert.Single(_errors);
    }

    [Fact]
    public void RecordAlerts_SwallowsAFailingSink()
    {
        var recorder = new HistoryRecorder(Conn, _repo, new ThrowingSink(), (m, _) => _errors.Add(m), _time);

        recorder.RecordAlerts([new AlertChange(AlertAt(T0).Alert, AlertChangeKind.Started)]);

        Assert.Single(_errors);
    }

    [Fact]
    public void RecordSample_SwallowsAFailingSink()
    {
        var recorder = new HistoryRecorder(Conn, _repo, new ThrowingSink(), (m, _) => _errors.Add(m), _time);

        recorder.RecordSample(Sample(1));

        Assert.Single(_errors);
    }

    [Fact]
    public async Task CollectDetail_FirstReadIsTheFullBaselineAndQueuesNothing()
    {
        _repo.Snapshots.Enqueue(Snap(ServerT0, Query("0xA", 10, 1_000)));

        await Create().CollectDetailIfDueAsync();

        Assert.Equal((null, HistoryRecorder.BaselineQueryLimit), Assert.Single(_repo.DetailCalls));
        Assert.Empty(_sink.Records);
    }

    [Fact]
    public async Task CollectDetail_WaitsForTheIntervalThenQueuesTheChangeWithQueryText()
    {
        var recorder = Create();
        _repo.Snapshots.Enqueue(Snap(ServerT0, Query("0xA", 10, 1_000)));
        await recorder.CollectDetailIfDueAsync();

        _time.Advance(DefaultInterval - TimeSpan.FromSeconds(1));
        await recorder.CollectDetailIfDueAsync();
        Assert.Single(_repo.DetailCalls);                       // not due yet

        _time.Advance(TimeSpan.FromSeconds(1));
        _repo.Snapshots.Enqueue(Snap(ServerT0.AddMinutes(5), Query("0xA", 15, 4_000)));
        await recorder.CollectDetailIfDueAsync();

        Assert.Equal(((DateTime?)ServerT0, HistoryRecorder.IntervalQueryLimit), _repo.DetailCalls[1]);
        var record = Assert.IsType<DetailRecord>(Assert.Single(_sink.Records));
        Assert.Equal(_time.GetUtcNow().UtcDateTime, record.CapturedUtc);
        Assert.Equal(new QueryDelta("0xA", 5, 3_000, 3_000, 0, 0), Assert.Single(record.Detail.TopQueries));
        Assert.Equal("SELECT * FROM dbo.Orders WHERE Code = ?", Assert.Single(record.QueryTexts).QueryText);
        Assert.Equal(new[] { "0xA" }, Assert.Single(_repo.TextCalls));
    }

    // What the fake server answers for a query's text: with a literal, which is personal data.
    private static string Text(string hash) => $"SELECT * FROM dbo.Orders WHERE Code = '{hash}'";

    // The baseline, then one snapshot of 0xA: its text, kept as the recorder's setting says.
    private async Task<IReadOnlyList<QueryTextInfo>> TextsKeptAsync(QueryTextStorage storage)
    {
        var recorder = Create();
        recorder.QueryTexts = storage;
        foreach (var (minutes, execs) in new[] { (0, 10L), (5, 15L) })
        {
            _repo.Snapshots.Enqueue(Snap(ServerT0.AddMinutes(minutes), Query("0xA", execs, execs * 100)));
            await recorder.CollectDetailIfDueAsync();
            _time.Advance(DefaultInterval);
        }
        return Assert.IsType<DetailRecord>(Assert.Single(_sink.Records)).QueryTexts;
    }

    [Fact]
    public void QueryTexts_AreKeptWithoutLiterals_UnlessSetOtherwise() =>
        Assert.Equal(QueryTextStorage.WithoutLiterals, Create().QueryTexts);

    [Fact]
    public async Task QueryTexts_Full_KeepsTheTextAsTheServerHasIt() =>
        Assert.Equal(Text("0xA"), Assert.Single(await TextsKeptAsync(QueryTextStorage.Full)).QueryText);

    [Fact]
    public async Task QueryTexts_Off_ReadsNoTextAtAll_ButKeepsTheNumbers()
    {
        Assert.Empty(await TextsKeptAsync(QueryTextStorage.Off));

        Assert.Empty(_repo.TextCalls);
        Assert.Single(((DetailRecord)_sink.Records[0]).Detail.TopQueries);
    }

    [Fact]
    public async Task QueryTexts_ChangedWhileRunning_ApplyToTextsReadFromThenOn()
    {
        var recorder = Create();
        recorder.QueryTexts = QueryTextStorage.Off;
        foreach (var (minutes, hash) in new[] { (0, "0xA"), (5, "0xA"), (10, "0xB") })
        {
            if (minutes == 10)
                recorder.QueryTexts = QueryTextStorage.Full;
            _repo.Snapshots.Enqueue(Snap(ServerT0.AddMinutes(minutes), Query("0xA", 10 + minutes, minutes * 100 + 1), Query("0xB", 10 + minutes, minutes * 100)));
            await recorder.CollectDetailIfDueAsync();
            _time.Advance(DefaultInterval);
        }

        // Off read none, so on Full both are read: the text of 0xA wasn't kept before.
        Assert.Empty(((DetailRecord)_sink.Records[0]).QueryTexts);
        Assert.Equal(new[] { Text("0xA"), Text("0xB") },
                     ((DetailRecord)_sink.Records[1]).QueryTexts.Select(t => t.QueryText).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CollectDetail_FetchesEachQueryTextOnce()
    {
        var recorder = Create();
        foreach (var (minutes, execs) in new[] { (0, 10L), (5, 15L), (10, 20L) })
        {
            _repo.Snapshots.Enqueue(Snap(ServerT0.AddMinutes(minutes), Query("0xA", execs, execs * 100)));
            await recorder.CollectDetailIfDueAsync();
            _time.Advance(DefaultInterval);
        }

        Assert.Single(_repo.TextCalls);
        Assert.Equal(2, _sink.Records.Count);
        Assert.Empty(((DetailRecord)_sink.Records[1]).QueryTexts);
    }

    [Fact]
    public async Task CollectDetail_ServerFailureIsReportedAndRetriedNextInterval()
    {
        var recorder = Create();
        _repo.DetailFailure = new InvalidOperationException("VIEW SERVER STATE permission was denied");

        await recorder.CollectDetailIfDueAsync();                  // must not throw
        await recorder.CollectDetailIfDueAsync();                  // not retried straight away

        Assert.Single(_repo.DetailCalls);
        Assert.Single(_errors);
        Assert.Empty(_sink.Records);

        _repo.DetailFailure = null;
        _repo.Snapshots.Enqueue(Snap(ServerT0));
        _time.Advance(DefaultInterval);
        await recorder.CollectDetailIfDueAsync();
        Assert.Equal(2, _repo.DetailCalls.Count);
    }

    [Fact]
    public async Task CollectDetail_TextFailureStillQueuesTheNumbers()
    {
        var recorder = Create();
        _repo.Snapshots.Enqueue(Snap(ServerT0, Query("0xA", 10, 1_000)));
        await recorder.CollectDetailIfDueAsync();

        _repo.TextFailure = new TimeoutException("Execution Timeout Expired");
        _repo.Snapshots.Enqueue(Snap(ServerT0.AddMinutes(5), Query("0xA", 15, 4_000)));
        _time.Advance(DefaultInterval);
        await recorder.CollectDetailIfDueAsync();

        var record = Assert.IsType<DetailRecord>(Assert.Single(_sink.Records));
        Assert.Single(record.Detail.TopQueries);
        Assert.Empty(record.QueryTexts);
        Assert.Single(_errors);
    }

    [Fact]
    public void DetailInterval_DefaultsToFiveMinutesAndMustBePositive()
    {
        var recorder = Create();

        Assert.Equal(TimeSpan.FromMinutes(5), recorder.DetailInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.DetailInterval = TimeSpan.Zero);
    }

    [Fact]
    public async Task CollectDetail_ShorterIntervalAppliesToTheNextCheck()
    {
        var recorder = Create();
        _repo.Snapshots.Enqueue(Snap(ServerT0));
        await recorder.CollectDetailIfDueAsync();

        recorder.DetailInterval = TimeSpan.FromMinutes(1);
        _time.Advance(TimeSpan.FromMinutes(1));
        _repo.Snapshots.Enqueue(Snap(ServerT0.AddMinutes(1)));
        await recorder.CollectDetailIfDueAsync();

        Assert.Equal(2, _repo.DetailCalls.Count);
    }

    [Fact]
    public async Task CollectDetail_LongerIntervalPutsOffTheNextSnapshot()
    {
        var recorder = Create();
        _repo.Snapshots.Enqueue(Snap(ServerT0));
        await recorder.CollectDetailIfDueAsync();

        recorder.DetailInterval = TimeSpan.FromMinutes(15);
        _time.Advance(DefaultInterval);
        await recorder.CollectDetailIfDueAsync();
        Assert.Single(_repo.DetailCalls);                       // 5 minutes is no longer enough

        _time.Advance(TimeSpan.FromMinutes(10));
        _repo.Snapshots.Enqueue(Snap(ServerT0.AddMinutes(15)));
        await recorder.CollectDetailIfDueAsync();
        Assert.Equal(2, _repo.DetailCalls.Count);
    }

    [Fact]
    public async Task CollectDetail_FetchesQueryTextAgainAfterHistoryIsCleared()
    {
        var recorder = Create();
        foreach (var (minutes, execs) in new[] { (0, 10L), (5, 15L), (10, 20L) })
        {
            if (minutes == 10)
                _sink.ClearCount++;                             // the saved text is gone
            _repo.Snapshots.Enqueue(Snap(ServerT0.AddMinutes(minutes), Query("0xA", execs, execs * 100)));
            await recorder.CollectDetailIfDueAsync();
            _time.Advance(DefaultInterval);
        }

        Assert.Equal(2, _repo.TextCalls.Count);
        Assert.Equal("SELECT * FROM dbo.Orders WHERE Code = ?", Assert.Single(((DetailRecord)_sink.Records[1]).QueryTexts).QueryText);
    }

    private sealed class FakeRepository : IHistoryRepository
    {
        public Queue<HistoryDetailSnapshot>                   Snapshots   { get; } = new();
        public List<(DateTime? Since, int Max)>              DetailCalls { get; } = new();
        public List<string[]>                                TextCalls   { get; } = new();
        public Exception?                                    DetailFailure { get; set; }
        public Exception?                                    TextFailure   { get; set; }
        public List<CancellationToken>                       Tokens      { get; } = new();

        public async Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Tokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            DetailCalls.Add((queriesExecutedSince, maxQueries));
            if (DetailFailure is not null) throw DetailFailure;
            return Snapshots.Dequeue();
        }

        public async Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Tokens.Add(cancellationToken);
            TextCalls.Add(queryHashes.ToArray());
            if (TextFailure is not null) throw TextFailure;
            Assert.Equal(HistoryRecorder.MaxQueryTextLength, maxLength);
            return queryHashes.Select(h => new QueryTextInfo(h, "Sales", Text(h))).ToList();
        }
    }

    private sealed class ListSink : IHistorySink
    {
        public List<HistoryRecord> Records { get; } = new();
        public int ClearCount { get; set; }
        public void Enqueue(HistoryRecord record) => Records.Add(record);
    }

    private sealed class ThrowingSink : IHistorySink
    {
        public void Enqueue(HistoryRecord record) => throw new InvalidOperationException("broken");
    }
}
