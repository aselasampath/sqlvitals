using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.History;
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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
