using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.History;

public sealed class HistoryStoreTests : IDisposable
{
    internal static readonly HistoryConnection Conn =
        new(Guid.Parse("6f1d2a8e-0000-4000-8000-000000000001"), "Prod", "sql01", "Sales");

    internal static readonly DateTime T0 = new(2026, 9, 23, 3, 30, 0, DateTimeKind.Utc);

    private readonly string _dir  = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "nested", "history.db");

    internal static LiveMetricSample Sample(double cpu, DateTime? serverTime = null) =>
        new(serverTime ?? new DateTime(2026, 9, 23, 9, 0, 0, 125),
            1, 2, 3, 4, 5, 6, cpu, 100, 7, 1, 2, 30, 40, 5000, 0, 99.5);

    internal static DetailRecord Detail(DateTime capturedUtc, params QueryTextInfo[] texts) =>
        new(Conn, capturedUtc,
            new HistoryDetail(
                new DateTime(2026, 9, 23, 9, 5, 0), 300,
                [new WaitTypeTotals("PAGEIOLATCH_SH", 12, 340, 4)],
                [new FileIoTotals("Sales", 1, "Sales_data", "ROWS", 10, 81920, 55, 2, 16384, 3)],
                [new QueryDelta("0xAB", 30, 600_000, 900_000, 1_200, 4)],
                new HistoryMemory(8_000_000, 16_000_000, 6_000_000, 500_000, 20_000, 100_000, 2)),
            texts);

    private SqliteConnection Raw()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    [Fact]
    public void Open_CreatesTheFolderAndSchemaInWalMode()
    {
        using (var store = new HistoryStore(DbPath))
            store.Open();

        using var raw = Raw();
        Assert.Equal(HistoryStore.SchemaVersion, raw.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal("wal", raw.ExecuteScalar<string>("PRAGMA journal_mode;"));
        Assert.Equal(2, raw.ExecuteScalar<long>("PRAGMA auto_vacuum;"));   // 2 = incremental
        Assert.Equal(
            new[] { "connections", "detail_snapshots", "file_io_deltas", "metric_samples", "query_deltas", "query_texts", "wait_deltas" },
            raw.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"));
    }

    [Fact]
    public void Open_TwiceKeepsExistingData()
    {
        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write([new MetricSampleRecord(Conn, T0, Sample(10))]);
        }

        using var again = new HistoryStore(DbPath);
        again.Open();
        Assert.Single(again.ReadSamples(Conn.Id, T0.AddHours(-1), T0.AddHours(1)));
    }

    [Fact]
    public void Write_SampleRoundTrips()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        var sample = Sample(42.5);

        store.Write([new MetricSampleRecord(Conn, T0, sample)]);

        var stored = Assert.Single(store.ReadSamples(Conn.Id, T0, T0.AddSeconds(1)));
        Assert.Equal(T0, stored.CapturedUtc);
        Assert.Equal(DateTimeKind.Utc, stored.CapturedUtc.Kind);
        Assert.Equal(sample, stored.Sample);
    }

    [Fact]
    public void Write_RecordsTheConnectionWithoutCredentials()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        store.Write([new MetricSampleRecord(Conn, T0, Sample(1))]);
        store.Write([new MetricSampleRecord(Conn with { Name = "Production" }, T0.AddSeconds(10), Sample(1))]);

        using var raw = Raw();
        var row = raw.QuerySingle("SELECT * FROM connections;");
        Assert.Equal(Conn.Id.ToString("D"), (string)row.connection_id);
        Assert.Equal("Production", (string)row.name);
        Assert.Equal("sql01", (string)row.server);
        Assert.Equal("Sales", (string)row.database_name);
    }

    [Fact]
    public void Write_DetailStoresEveryPart()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();

        store.Write([Detail(T0, new QueryTextInfo("0xAB", "Sales", "SELECT * FROM dbo.Orders WHERE Id = @p"))]);

        using var raw = Raw();
        var snap = raw.QuerySingle("SELECT * FROM detail_snapshots;");
        Assert.Equal(300.0, (double)snap.interval_sec);
        Assert.Equal("2026-09-23 09:05:00.000", (string)snap.server_time);
        Assert.Equal(8_000_000L, (long)snap.total_server_memory_kb);
        Assert.Equal(340L, raw.ExecuteScalar<long>("SELECT wait_time_ms FROM wait_deltas WHERE wait_type = 'PAGEIOLATCH_SH';"));
        Assert.Equal(55L, raw.ExecuteScalar<long>("SELECT read_stall_ms FROM file_io_deltas WHERE database_name = 'Sales';"));
        Assert.Equal(600_000L, raw.ExecuteScalar<long>("SELECT worker_time_us FROM query_deltas WHERE query_hash = '0xAB';"));
        Assert.Equal("SELECT * FROM dbo.Orders WHERE Id = @p", raw.ExecuteScalar<string>("SELECT query_text FROM query_texts;"));
    }

    [Fact]
    public void Write_DetailWithoutMemoryStoresNulls()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        var record = Detail(T0);

        store.Write([record with { Detail = record.Detail with { Memory = null } }]);

        using var raw = Raw();
        Assert.Null(raw.ExecuteScalar<long?>("SELECT total_server_memory_kb FROM detail_snapshots;"));
    }

    [Fact]
    public void Purge_RemovesOnlyRowsOlderThanTheCutoff()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        var old    = T0.AddDays(-20);
        var recent = T0.AddDays(-1);
        store.Write(
        [
            new MetricSampleRecord(Conn, old, Sample(1)),
            new MetricSampleRecord(Conn, old.AddDays(3), Sample(2)),   // spans more than one purge chunk
            new MetricSampleRecord(Conn, recent, Sample(3)),
            Detail(old, new QueryTextInfo("0xAB", "Sales", "old text")),
            Detail(recent),
        ]);

        store.Purge(T0.AddDays(-14));

        Assert.Equal(new[] { 3.0 }, store.ReadSamples(Conn.Id, T0.AddYears(-1), T0).Select(s => s.Sample.SqlCpuPct));
        using var raw = Raw();
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM detail_snapshots;"));
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM wait_deltas;"));
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM file_io_deltas;"));
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM query_deltas;"));
        // The recent snapshot saw 0xAB again, so its text (first stored 20 days ago) is kept.
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM query_texts;"));
    }

    [Fact]
    public void Purge_DropsTextOfQueriesNotSeenSinceTheCutoff()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        store.Write([Detail(T0.AddDays(-20), new QueryTextInfo("0xAB", "Sales", "old text"))]);

        store.Purge(T0.AddDays(-14));

        using var raw = Raw();
        Assert.Equal(0L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM query_texts;"));
    }

    [Fact]
    public void Open_FileFromANewerVersionIsLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using (var raw = Raw())
            raw.Execute("PRAGMA user_version = 99; CREATE TABLE future (x);");

        using var store = new HistoryStore(DbPath);
        var ex = Assert.Throws<HistorySchemaTooNewException>(store.Open);

        Assert.Equal(99, ex.Version);
        using var check = Raw();
        Assert.Equal(99L, check.ExecuteScalar<long>("PRAGMA user_version;"));
    }

    [Fact]
    public void Open_CorruptFileIsMovedAsideAndReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllText(DbPath, "this is not a database, it is a text file that is long enough to have a header");

        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write([new MetricSampleRecord(Conn, T0, Sample(5))]);
        }

        var quarantined = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "history.corrupt-*.db"));
        Assert.StartsWith("this is not a database", File.ReadAllText(quarantined));
        using var raw = Raw();
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM metric_samples;"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
