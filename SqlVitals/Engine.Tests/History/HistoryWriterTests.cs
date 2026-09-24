using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using static SqlVitals.Engine.Tests.History.HistoryStoreTests;

namespace SqlVitals.Engine.Tests.History;

public sealed class HistoryWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _errors = new();

    private string DbPath => Path.Combine(_dir, "history.db");

    private HistoryWriter Create(string? path = null, TimeProvider? time = null) =>
        new(path ?? DbPath, (message, _) => _errors.Enqueue(message), TimeSpan.FromDays(14), time);

    private long Count(string table)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        conn.Open();
        return conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table};");
    }

    [Fact]
    public void Dispose_SavesEverythingQueued()
    {
        using (var writer = Create())
        {
            for (var i = 0; i < 25; i++)
                writer.Enqueue(new MetricSampleRecord(Conn, T0.AddSeconds(10 * i), Sample(i)));
            writer.Enqueue(Detail(T0));
        }

        Assert.Equal(25, Count("metric_samples"));
        Assert.Equal(1, Count("detail_snapshots"));
        Assert.Empty(_errors);
    }

    [Fact]
    public void Enqueue_AfterDisposeIsIgnored()
    {
        var writer = Create();
        writer.Dispose();

        writer.Enqueue(new MetricSampleRecord(Conn, T0, Sample(1)));   // must not throw
        writer.Dispose();                                              // nor a second Dispose
    }

    [Fact]
    public void UnusablePath_IsReportedNotThrown()
    {
        // A file where the folder should be: the store can never be created.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "");

        using (var writer = Create(Path.Combine(blocker, "history.db")))
            writer.Enqueue(new MetricSampleRecord(Conn, T0, Sample(1)));

        Assert.Contains(_errors, e => e.StartsWith("Could not open the monitoring history file"));
    }

    [Fact]
    public void FileFromANewerVersion_TurnsHistoryOff()
    {
        Directory.CreateDirectory(_dir);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString()))
        {
            conn.Open();
            conn.Execute("PRAGMA user_version = 99;");
        }

        var writer = Create();
        writer.Enqueue(new MetricSampleRecord(Conn, T0, Sample(1)));
        writer.Dispose();

        Assert.True(writer.IsDisabled);
        Assert.Contains(_errors, e => e.Contains("schema version 99"));
    }

    [Fact]
    public void FirstBatch_PurgesHistoryOlderThanTheRetention()
    {
        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write([new MetricSampleRecord(Conn, T0.AddDays(-30), Sample(1))]);
        }

        using (var writer = Create(time: new ManualTime(new DateTimeOffset(T0))))
            writer.Enqueue(new MetricSampleRecord(Conn, T0, Sample(2)));

        Assert.Equal(1, Count("metric_samples"));
    }

    [Fact]
    public async Task ClearAsync_DeletesEverythingIncludingRecordsStillQueued()
    {
        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write([new MetricSampleRecord(Conn, T0.AddHours(-1), Sample(1)),
                         Detail(T0.AddHours(-1), new QueryTextInfo("0xAB", "Sales", "SELECT 1"))]);
        }

        using var writer = Create();
        writer.Enqueue(new MetricSampleRecord(Conn, T0, Sample(2)));
        await writer.ClearAsync();

        foreach (var table in new[] { "metric_samples", "detail_snapshots", "wait_deltas", "file_io_deltas",
                                      "query_deltas", "query_texts", "connections" })
            Assert.Equal(0, Count(table));
        Assert.Equal(1, writer.ClearCount);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ClearAsync_RecordsQueuedAfterwardsAreKept()
    {
        using (var writer = Create())
        {
            writer.Enqueue(new MetricSampleRecord(Conn, T0, Sample(1)));
            var clear = writer.ClearAsync();
            writer.Enqueue(new MetricSampleRecord(Conn, T0.AddSeconds(10), Sample(2)));
            await clear;
        }

        Assert.Equal(1, Count("metric_samples"));
    }

    [Fact]
    public async Task ClearAsync_WithNothingSavedYetCreatesNoFile()
    {
        using var writer = Create();

        await writer.ClearAsync();

        Assert.False(File.Exists(DbPath));
        Assert.Equal(0, writer.ClearCount);
    }

    [Fact]
    public async Task ClearAsync_AfterDisposeFails()
    {
        var writer = Create();
        writer.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(writer.ClearAsync);
    }

    [Fact]
    public async Task ClearAsync_FileFromANewerVersionFailsAndIsLeftAlone()
    {
        Directory.CreateDirectory(_dir);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString()))
        {
            conn.Open();
            conn.Execute("PRAGMA user_version = 99; CREATE TABLE future (x); INSERT INTO future VALUES (1);");
        }

        using var writer = Create();
        await Assert.ThrowsAsync<HistorySchemaTooNewException>(writer.ClearAsync);

        Assert.True(writer.IsDisabled);
        Assert.Equal(1, Count("future"));
    }

    [Fact]
    public void Retention_ShortenedPurgesStraightAwayWithoutWaitingForARecord()
    {
        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write([new MetricSampleRecord(Conn, T0.AddDays(-10), Sample(1)),
                         new MetricSampleRecord(Conn, T0.AddDays(-1), Sample(2))]);
        }

        using (var writer = new HistoryWriter(DbPath, (m, _) => _errors.Enqueue(m), TimeSpan.FromDays(30),
                                              new ManualTime(new DateTimeOffset(T0))))
        {
            writer.Retention = TimeSpan.FromDays(90);      // longer: nothing to delete
            writer.Retention = TimeSpan.FromDays(7);
        }

        Assert.Equal(1, Count("metric_samples"));
        Assert.Empty(_errors);
    }

    [Fact]
    public void Retention_ShortenedWithNoHistoryCreatesNoFile()
    {
        using (var writer = Create())
            writer.Retention = TimeSpan.FromDays(7);

        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void Retention_MustBePositive()
    {
        using var writer = Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Retention = TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromDays(14), writer.Retention);
    }

    [Fact]
    public void EndAlertsLeftOpen_RunsBeforeRecordsQueuedAfterIt()
    {
        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write([AlertAt(T0.AddMinutes(-10))]);   // left active by the "previous run"
        }

        var time = new ManualTime(new DateTimeOffset(T0));
        using (var writer = Create(time: time))
        {
            writer.EndAlertsLeftOpen();
            writer.Enqueue(AlertAt(T0));                  // this run's alert, queued straight after
        }

        Assert.Equal(1, Count("alerts WHERE ended_utc IS NULL"));
        Assert.Equal(1, Count("alerts WHERE end_reason = 'MonitoringStopped'"));
        Assert.Empty(_errors);
    }

    [Fact]
    public void EndAlertsLeftOpen_WithNoHistoryCreatesNoFile()
    {
        using (var writer = Create())
            writer.EndAlertsLeftOpen();

        Assert.False(File.Exists(DbPath));
        Assert.Empty(_errors);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
