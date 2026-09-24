using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Tests.History;

public sealed class HistoryReaderTests : IDisposable
{
    private static readonly HistoryConnection Conn = HistoryStoreTests.Conn;
    private static readonly HistoryConnection Other = new(Guid.Parse("6f1d2a8e-0000-4000-8000-000000000002"), "Test", "sql02", "Sales");
    private static readonly DateTime T0 = HistoryStoreTests.T0;   // 03:30:00 UTC, a whole minute

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "history.db");

    private HistoryStore OpenStore()
    {
        var store = new HistoryStore(DbPath);
        store.Open();
        return store;
    }

    private static DetailRecord Detail(HistoryConnection conn, DateTime capturedUtc, double seconds,
                                       IReadOnlyList<WaitTypeTotals> waits, IReadOnlyList<CounterValue>? counters = null) =>
        new(conn, capturedUtc,
            new HistoryDetail(capturedUtc, seconds, waits, [], [], null, counters),
            []);

    [Fact]
    public void Read_WithoutAFileReturnsNothingAndCreatesNothing()
    {
        var reader = new HistoryReader(DbPath);

        Assert.Empty(reader.ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromSeconds(10)));
        Assert.Empty(reader.ReadWaits(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromSeconds(10)));
        Assert.Empty(reader.ReadCounters(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void ReadMetrics_ReturnsOneConnectionsSamplesInTheRangeOldestFirst()
    {
        using (var store = OpenStore())
            store.Write(
            [
                new MetricSampleRecord(Conn,  T0.AddSeconds(20), HistoryStoreTests.Sample(30)),
                new MetricSampleRecord(Conn,  T0,                HistoryStoreTests.Sample(10)),
                new MetricSampleRecord(Conn,  T0.AddHours(1),    HistoryStoreTests.Sample(99)),   // end is excluded
                new MetricSampleRecord(Other, T0.AddSeconds(10), HistoryStoreTests.Sample(50)),
            ]);

        var points = new HistoryReader(DbPath).ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { T0, T0.AddSeconds(20) }, points.Select(p => p.TimeUtc));
        Assert.Equal(new[] { 10.0, 30.0 }, points.Select(p => p.Averages.SqlCpuPct));
        Assert.All(points, p => Assert.Equal(DateTimeKind.Utc, p.TimeUtc.Kind));
    }

    [Fact]
    public void ReadMetrics_AveragesEachBucket()
    {
        using (var store = OpenStore())
            store.Write(Enumerable.Range(0, 12)
                .Select(i => (HistoryRecord)new MetricSampleRecord(Conn, T0.AddSeconds(10 * i), HistoryStoreTests.Sample(i)))
                .ToList());

        var points = new HistoryReader(DbPath).ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1));

        // Samples 0–5 fall in the first minute, 6–11 in the second.
        Assert.Equal(new[] { 2.5, 8.5 }, points.Select(p => p.Averages.SqlCpuPct));
        Assert.Equal(new[] { T0.AddSeconds(25), T0.AddSeconds(85) }, points.Select(p => p.TimeUtc));
    }

    [Fact]
    public void ReadMetrics_GivesEachBucketsTimeOnTheServersClock()
    {
        // A server 5:30 ahead of UTC, as Live Metrics plots it.
        var offset = TimeSpan.FromHours(5.5);
        using (var store = OpenStore())
            store.Write(Enumerable.Range(0, 6)
                .Select(i => (HistoryRecord)new MetricSampleRecord(Conn, T0.AddSeconds(10 * i),
                    HistoryStoreTests.Sample(i, serverTime: DateTime.SpecifyKind(T0.AddSeconds(10 * i) + offset, DateTimeKind.Unspecified))))
                .ToList());

        var point = Assert.Single(new HistoryReader(DbPath).ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1)));

        Assert.Equal(offset, point.ServerClockOffset);
        Assert.Equal(T0.AddSeconds(25) + offset, point.ServerTime);
        Assert.Equal(T0 + offset, point.Averages.Time);   // the first sample's server time
    }

    [Fact]
    public void ReadWaits_SumsEachBucketAndCountsItsSeconds()
    {
        using (var store = OpenStore())
            store.Write(
            [
                Detail(Conn, T0,                300, [new("PAGEIOLATCH_SH", 10, 3_000, 30), new("LCK_M_X", 1, 600, 0)]),
                Detail(Conn, T0.AddMinutes(5),  300, [new("PAGEIOLATCH_SH", 20, 6_000, 60)]),
                Detail(Conn, T0.AddMinutes(10), 300, []),
                Detail(Other, T0,               300, [new("WRITELOG", 5, 50, 5)]),
            ]);

        var buckets = new HistoryReader(DbPath).ReadWaits(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(10));

        Assert.Equal(2, buckets.Count);
        Assert.Equal(600, buckets[0].Seconds);
        Assert.Equal(T0.AddMinutes(2.5), buckets[0].TimeUtc);
        Assert.Equal(new WaitTypeTotals("PAGEIOLATCH_SH", 30, 9_000, 90), buckets[0].Waits["PAGEIOLATCH_SH"]);
        Assert.Equal(new WaitTypeTotals("LCK_M_X", 1, 600, 0), buckets[0].Waits["LCK_M_X"]);

        // A snapshot with no waits still counts: the server was watched and nothing waited.
        Assert.Equal(300, buckets[1].Seconds);
        Assert.Empty(buckets[1].Waits);
    }

    [Fact]
    public void ReadCounters_AveragesOverEverySnapshotWithCountersZerosIncluded()
    {
        using (var store = OpenStore())
            store.Write(
            [
                Detail(Conn, T0,               300, [], [new("Batch Requests/sec", 100), new("Page life expectancy", 900)]),
                Detail(Conn, T0.AddMinutes(5), 300, [], [new("Page life expectancy", 1_100)]),   // no batches: zero
            ]);

        var bucket = Assert.Single(new HistoryReader(DbPath).ReadCounters(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(10)));

        Assert.Equal(50, bucket.Values["Batch Requests/sec"]);
        Assert.Equal(1_000, bucket.Values["Page life expectancy"]);
    }

    [Fact]
    public void ReadCounters_LeavesOutSnapshotsFromBeforeCountersWereRecorded()
    {
        using (var store = OpenStore())
            store.Write(
            [
                Detail(Conn, T0,               300, [new("CXPACKET", 1, 10, 0)]),                  // older SqlVitals
                Detail(Conn, T0.AddMinutes(5), 300, [], [new("Batch Requests/sec", 40)]),
            ]);

        var buckets = new HistoryReader(DbPath).ReadCounters(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1));

        var bucket = Assert.Single(buckets);
        Assert.Equal(T0.AddMinutes(5), bucket.TimeUtc);
        Assert.Equal(40, bucket.Values["Batch Requests/sec"]);
    }

    [Fact]
    public void ReadCounters_FromAVersion1FileReturnsNothing()
    {
        Directory.CreateDirectory(_dir);
        using (var raw = Raw())
            raw.Execute("PRAGMA user_version = 1; CREATE TABLE detail_snapshots (snapshot_id INTEGER, connection_id TEXT, captured_utc INTEGER);");

        Assert.Empty(new HistoryReader(DbPath).ReadCounters(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Read_FileFromANewerVersionThrowsAndIsLeftAlone()
    {
        Directory.CreateDirectory(_dir);
        using (var raw = Raw())
            raw.Execute("PRAGMA user_version = 99; CREATE TABLE future (x);");

        var ex = Assert.Throws<HistorySchemaTooNewException>(
            () => new HistoryReader(DbPath).ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1)));

        Assert.Equal(99, ex.Version);
        using var check = Raw();
        Assert.Equal(99L, check.ExecuteScalar<long>("PRAGMA user_version;"));
    }

    [Fact]
    public void Read_SeesWhatTheWriterCommittedWhileItKeepsTheFileOpen()
    {
        using var store = OpenStore();
        store.Write([new MetricSampleRecord(Conn, T0, HistoryStoreTests.Sample(10))]);
        var reader = new HistoryReader(DbPath);

        Assert.Single(reader.ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromSeconds(10)));

        store.Write([new MetricSampleRecord(Conn, T0.AddSeconds(10), HistoryStoreTests.Sample(20))]);
        Assert.Equal(2, reader.ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromSeconds(10)).Count);

        // The reader holds nothing open afterwards, so the writer can still clear and compact.
        store.Clear();
    }

    [Fact]
    public void Read_SurvivesARestart()
    {
        // What "history is still there after restarting the app" comes down to: a new writer
        // and a new reader over the same file.
        // Recent, so the writer's retention purge leaves it alone.
        var at     = DateTime.UtcNow.AddMinutes(-1);
        var errors = new List<Exception>();
        using (var writer = new HistoryWriter(DbPath, (_, ex) => errors.Add(ex)))
            writer.Enqueue(new MetricSampleRecord(Conn, at, HistoryStoreTests.Sample(42)));

        Assert.Empty(errors);
        var point = Assert.Single(new HistoryReader(DbPath).ReadMetrics(Conn.Id, at.AddHours(-1), at.AddHours(1), TimeSpan.FromSeconds(10)));
        Assert.Equal(42, point.Averages.SqlCpuPct);
    }

    private SqliteConnection Raw()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
