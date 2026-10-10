using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using static SqlVitals.Engine.Tests.History.HistoryStoreTests;

namespace SqlVitals.Engine.Tests.History;

/// <summary>
/// The app's history and a collector's as one (#172): a server watched by the app, then by the
/// service, reads as one history under the app's id; a collector's file that can't be read is
/// left out and said; and the share copy another PC reads.
/// </summary>
public sealed class CombinedHistoryReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, params HistoryRecord[] records)
    {
        var path = Path.Combine(_dir, name);
        using var store = new HistoryStore(path);
        store.Open();
        store.Write(records);
        return path;
    }

    private static HistoryRecord[] Watched(HistoryConnection conn, DateTime fromUtc, int minutes, double cpu) =>
        Enumerable.Range(0, minutes).SelectMany(i => new HistoryRecord[]
        {
            new MetricSampleRecord(conn, fromUtc.AddMinutes(i), Sample(cpu)),
            new HealthScoreRecord(conn, fromUtc.AddMinutes(i), 90, true),
        }).ToArray();

    [Fact]
    public void AServerHandedOver_ReadsAsOneHistory_InTimeOrder()
    {
        var app     = Write("app.db", Watched(Conn, T0, 10, cpu: 10));
        var service = Write("service.db", Watched(Conn, T0.AddMinutes(10), 10, cpu: 20));
        var reader  = new CombinedHistoryReader(new HistoryReader(app), [new HistorySource(new HistoryReader(service), "the collector service")]);

        var metrics = reader.ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1));
        var scores  = reader.ReadScores(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1));

        Assert.Equal(20, metrics.Count);
        Assert.Equal(metrics.OrderBy(p => p.TimeUtc), metrics);
        Assert.Equal([10, 20], metrics.Select(p => p.Averages.SqlCpuPct).Distinct());
        Assert.Equal(20, scores.Count);
        Assert.Equal(T0.AddMinutes(19), reader.ReadLastSampleTime(Conn.Id));
        Assert.Equal(app, reader.Path);
    }

    [Fact]
    public void AnotherPcsCollector_IsReadUnderTheAppsId_ByItsMap()
    {
        var theirs  = new HistoryConnection(Guid.NewGuid(), "SALES", "SQL01", "Sales");
        var alert   = AlertAt(T0.AddMinutes(5), connectionId: theirs.Id) with { Connection = theirs };
        var remote  = Write("remote.db", [.. Watched(theirs, T0, 10, cpu: 30), alert]);
        var app     = Write("app.db", Watched(Conn, T0.AddHours(-1), 1, cpu: 5));
        var reader  = new CombinedHistoryReader(new HistoryReader(app),
            [new HistorySource(new HistoryReader(remote, readOnly: true), "the collector at \\\\dba01\\share", new Dictionary<Guid, Guid> { [Conn.Id] = theirs.Id })]);

        Assert.Equal(10, reader.ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1)).Count);
        var alerts = reader.ReadAlerts(null, T0, T0.AddHours(1));
        Assert.Equal(Conn.Id, Assert.Single(alerts).Alert.ConnectionId);
        Assert.Equal(Conn.Id, Assert.Single(reader.ReadAlerts(Conn.Id, T0, T0.AddHours(1))).Alert.ConnectionId);
        Assert.Contains(reader.ReadConnections(), c => c.Id == Conn.Id && c.Name == "SALES");
        // A connection the map doesn't name reads nothing from that file.
        Assert.Empty(reader.ReadMetrics(theirs.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ACollectorsFileThatCantBeRead_IsLeftOutAndSaid_TheAppsStillShows()
    {
        var app = Write("app.db", Watched(Conn, T0, 5, cpu: 10));
        var broken = Path.Combine(_dir, "broken.db");
        File.WriteAllText(broken, "not a database at all, just text long enough to have a header");
        var failed = new List<string>();
        var reader = new CombinedHistoryReader(new HistoryReader(app), [new HistorySource(new HistoryReader(broken), "the collector service")],
                                               (source, _) => failed.Add(source.Name));

        Assert.Equal(5, reader.ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1)).Count);
        Assert.Equal(["the collector service"], failed);
    }

    [Fact]
    public void QueryWorkInBothFiles_AddsUp_AndActivityIsNewestFirst()
    {
        var app     = Write("app.db", Detail(T0), new ActivityRecord(new ActivityEntry(T0, ActivityEvents.MonitoringStopped, Conn.Id, "Prod", "app")));
        var service = Write("service.db", Detail(T0.AddMinutes(30)),
                            new ActivityRecord(new ActivityEntry(T0.AddMinutes(1), ActivityEvents.MonitoringStarted, Conn.Id, "Prod", ActivitySources.CollectorService)));
        var reader  = new CombinedHistoryReader(new HistoryReader(app), [new HistorySource(new HistoryReader(service), "the collector service")]);

        var totals = Assert.Single(reader.ReadQueryTotals(Conn.Id, T0.AddMinutes(-1), T0.AddHours(1)));
        Assert.Equal(60, totals.Executions);
        Assert.Equal([ActivityEvents.MonitoringStarted, ActivityEvents.MonitoringStopped],
                     reader.ReadActivity(T0.AddMinutes(-1), T0.AddHours(1), Conn.Id).Select(r => r.Entry.Event));
    }

    [Fact]
    public void TheShareCopy_IsTheWholeHistory_InRollbackJournalMode_ReadReadOnly()
    {
        var live   = Write("history.db", [.. Watched(Conn, T0, 10, cpu: 10), AlertAt(T0.AddMinutes(2))]);
        var target = Path.Combine(_dir, "share", HistoryShareCopy.FileName);

        Assert.True(HistoryShareCopy.Write(live, target));
        Assert.True(HistoryShareCopy.Write(live, target));   // again, over the first

        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            raw.Open();
            Assert.Equal("delete", raw.ExecuteScalar<string>("PRAGMA journal_mode;"));
        }
        var copy = new HistoryReader(target, readOnly: true);
        Assert.Equal(10, copy.ReadMetrics(Conn.Id, T0, T0.AddHours(1), TimeSpan.FromMinutes(1)).Count);
        Assert.Single(copy.ReadAlerts(Conn.Id, T0, T0.AddHours(1)));
        Assert.Equal(("Prod", "sql01", "Sales"), (copy.ReadConnections()[0].Name, copy.ReadConnections()[0].Server, copy.ReadConnections()[0].Database));
        Assert.False(File.Exists(target + ".tmp"));
    }
}
