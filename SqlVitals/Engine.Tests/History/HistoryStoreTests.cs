using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.Alerts;
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
                new HistoryMemory(8_000_000, 16_000_000, 6_000_000, 500_000, 20_000, 100_000, 2),
                [new CounterValue("Batch Requests/sec", 125.5)]),
            texts);

    /// <summary>A CPU warning alert of <see cref="Conn"/>, active unless <paramref name="endedUtc"/> is given.</summary>
    internal static AlertRecord AlertAt(DateTime startedUtc, DateTime? endedUtc = null, Guid? id = null, Guid? connectionId = null) =>
        new(Conn, endedUtc ?? startedUtc,
            new Alert(id ?? Guid.NewGuid(), connectionId ?? Conn.Id, HealthIndicator.Cpu, HealthLevel.Warning,
                      startedUtc, endedUtc, 82, 75, "CPU 82%",
                      endedUtc is null ? null : AlertEndReason.Recovered));

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
            new[] { "alerts", "connections", "counter_values", "detail_snapshots", "file_io_deltas", "metric_samples", "query_deltas", "query_texts", "wait_deltas" },
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
        Assert.Equal(125.5, raw.ExecuteScalar<double>("SELECT value FROM counter_values WHERE counter_name = 'Batch Requests/sec';"));
    }

    [Fact]
    public void Open_UpgradesAVersion1FileAndKeepsItsData()
    {
        // The tables exactly as SqlVitals 0.29 created them: everything but counter_values.
        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write([new MetricSampleRecord(Conn, T0, Sample(10))]);
        }
        using (var raw = Raw())
            raw.Execute("DROP TABLE counter_values; PRAGMA user_version = 1;");

        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            Assert.Single(store.ReadSamples(Conn.Id, T0, T0.AddSeconds(1)));
            store.Write([Detail(T0)]);
        }

        using var check = Raw();
        Assert.Equal(HistoryStore.SchemaVersion, check.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(1L, check.ExecuteScalar<long>("SELECT COUNT(*) FROM counter_values;"));
    }

    [Fact]
    public void Open_UpgradesAVersion2FileAndKeepsItsData()
    {
        // The tables exactly as SqlVitals 0.30–0.33 created them: everything but alerts.
        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write([new MetricSampleRecord(Conn, T0, Sample(10)), Detail(T0)]);
        }
        using (var raw = Raw())
            raw.Execute("DROP TABLE alerts; PRAGMA user_version = 2;");

        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            Assert.Single(store.ReadSamples(Conn.Id, T0, T0.AddSeconds(1)));
            store.Write([AlertAt(T0)]);
        }

        using var check = Raw();
        Assert.Equal(3L, check.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(1L, check.ExecuteScalar<long>("SELECT COUNT(*) FROM detail_snapshots;"));
        Assert.Equal(1L, check.ExecuteScalar<long>("SELECT COUNT(*) FROM alerts;"));
    }

    [Fact]
    public void Write_AlertIsStoredAndALaterRecordOfItReplacesIt()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        var id = Guid.NewGuid();

        store.Write([AlertAt(T0, id: id)]);
        using (var raw = Raw())
        {
            var row = raw.QuerySingle("SELECT * FROM alerts;");
            Assert.Equal(id.ToString("D"), (string)row.alert_id);
            Assert.Equal(Conn.Id.ToString("D"), (string)row.connection_id);
            Assert.Equal("Cpu", (string)row.indicator);
            Assert.Equal("Warning", (string)row.severity);
            Assert.Equal(HistoryStore.ToEpochMs(T0), (long)row.started_utc);
            Assert.Null(row.ended_utc);
            Assert.Equal((82.0, 75.0, "CPU 82%"), ((double)row.value, (double)row.threshold, (string)row.detail));
            Assert.Null(row.end_reason);
        }

        store.Write([AlertAt(T0, T0.AddMinutes(4), id)]);

        using var after = Raw();
        var ended = after.QuerySingle("SELECT * FROM alerts;");
        Assert.Equal(HistoryStore.ToEpochMs(T0.AddMinutes(4)), (long)ended.ended_utc);
        Assert.Equal("Recovered", (string)ended.end_reason);
    }

    [Fact]
    public void EndAlertsLeftOpen_EndsThemAtTheLastSampleTheirConnectionStored()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        var otherConn = Conn with { Id = Guid.NewGuid(), Name = "Test" };
        var other     = otherConn.Id;
        store.Write(
        [
            AlertAt(T0),                                              // left active: ends at the last sample
            AlertAt(T0.AddMinutes(20)),                               // left active, no sample since: ends at its start
            AlertAt(T0.AddMinutes(-30), T0.AddMinutes(-25)),          // already ended: untouched
            new MetricSampleRecord(Conn, T0.AddMinutes(2), Sample(80)),
            new MetricSampleRecord(Conn, T0.AddMinutes(5), Sample(80)),
            AlertAt(T0, connectionId: other) with { Connection = otherConn },   // another connection, never sampled
        ]);

        Assert.Equal(3, store.EndAlertsLeftOpen());

        using var raw = Raw();
        var rows = raw.Query("SELECT started_utc, ended_utc, end_reason, connection_id FROM alerts ORDER BY connection_id = @other, started_utc;",
                             new { other = other.ToString("D") }).ToList();
        Assert.Equal(HistoryStore.ToEpochMs(T0.AddMinutes(-25)), (long)rows[0].ended_utc);
        Assert.Equal("Recovered", (string)rows[0].end_reason);
        Assert.Equal(HistoryStore.ToEpochMs(T0.AddMinutes(5)), (long)rows[1].ended_utc);
        Assert.Equal("MonitoringStopped", (string)rows[1].end_reason);
        Assert.Equal(HistoryStore.ToEpochMs(T0.AddMinutes(20)), (long)rows[2].ended_utc);
        Assert.Equal(HistoryStore.ToEpochMs(T0), (long)rows[3].ended_utc);
        Assert.Equal(0, store.EndAlertsLeftOpen());
    }

    [Fact]
    public void Purge_RemovesAlertsThatEndedBeforeTheCutoffAndKeepsActiveOnes()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        store.Write(
        [
            AlertAt(T0.AddDays(-20), T0.AddDays(-19)),   // ended long ago: purged
            AlertAt(T0.AddDays(-20), T0.AddDays(-1)),    // started long ago, ended recently: kept
            AlertAt(T0.AddDays(-20)),                    // still active: kept
        ]);

        store.Purge(T0.AddDays(-14));

        using var raw = Raw();
        Assert.Equal(2L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM alerts;"));
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM alerts WHERE ended_utc IS NULL;"));
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
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM counter_values;"));
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
    public void Clear_DeletesEverythingAndShrinksTheFile()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        store.Write(Enumerable.Range(0, 20_000)
                              .Select(i => (HistoryRecord)new MetricSampleRecord(Conn, T0.AddSeconds(10 * i), Sample(i)))
                              .Append(Detail(T0, new QueryTextInfo("0xAB", "Sales", "SELECT 1")))
                              .Append(AlertAt(T0))
                              .ToList());
        store.Purge(T0.AddYears(-1));   // checkpoints the WAL into the file, so the size below is the data
        var before = HistoryStore.SizeOnDisk(DbPath);

        store.Clear();

        var after = HistoryStore.SizeOnDisk(DbPath);
        Assert.True(after < before / 10, $"{before:N0} bytes before, {after:N0} after");
        using var raw = Raw();
        foreach (var table in new[] { "connections", "metric_samples", "detail_snapshots", "wait_deltas",
                                      "file_io_deltas", "query_deltas", "query_texts", "counter_values", "alerts" })
            Assert.Equal(0L, raw.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public void Clear_LeavesTheStoreReadyForNewRecords()
    {
        using var store = new HistoryStore(DbPath);
        store.Open();
        store.Write([new MetricSampleRecord(Conn, T0, Sample(1))]);

        store.Clear();
        store.Write([new MetricSampleRecord(Conn, T0.AddSeconds(10), Sample(2))]);

        Assert.Equal(2.0, Assert.Single(store.ReadSamples(Conn.Id, T0, T0.AddMinutes(1))).Sample.SqlCpuPct);
    }

    [Fact]
    public void SizeOnDisk_CountsTheWalFilesAndIsZeroWithoutAFile()
    {
        Assert.Equal(0, HistoryStore.SizeOnDisk(DbPath));

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllBytes(DbPath, new byte[4096]);
        File.WriteAllBytes(DbPath + "-wal", new byte[1000]);

        Assert.Equal(5096, HistoryStore.SizeOnDisk(DbPath));
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
