using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Tests.Activity;
using static SqlVitals.Engine.Tests.History.HistoryStoreTests;

namespace SqlVitals.Engine.Tests.History;

/// <summary>The activity log (#115) in history.db: schema 4, written through the store and the writer.</summary>
public sealed class ActivityLogTests : IDisposable
{
    private static readonly Guid ConnId = Guid.Parse("6f1d2a8e-0000-4000-8000-000000000001");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string>        _errors   = new();
    private readonly ConcurrentQueue<ActivityEntry> _notSaved = new();

    private string DbPath => Path.Combine(_dir, "history.db");

    private static ActivityEntry Entry(string evt, DateTime timeUtc, string source = ActivitySources.Settings,
                                       Guid? connectionId = null, string? connectionName = null) =>
        new(timeUtc, evt, connectionId, connectionName, source) { WindowsUser = @"CONTOSO\dba", Machine = "PC01" };

    private HistoryWriter CreateWriter(TimeProvider? time = null) =>
        new(DbPath, (m, _) => _errors.Enqueue(m), TimeSpan.FromDays(14), time, _notSaved.Enqueue)
        {
            DisposeWait = HistoryWriterTests.DisposeWait,
        };

    private HistoryStore OpenStore()
    {
        var store = new HistoryStore(DbPath);
        store.Open();
        return store;
    }

    private SqliteConnection Raw()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private IReadOnlyList<ActivityEntry> ReadAll() =>
        new HistoryReader(DbPath).ReadActivity(T0.AddYears(-5), T0.AddYears(5)).Select(r => r.Entry).ToList();

    [Fact]
    public void Open_UpgradesAVersion3FileAndKeepsItsData()
    {
        // The tables exactly as SqlVitals 0.34–0.45 created them: everything but activity_log.
        using (var store = OpenStore())
            store.Write([new MetricSampleRecord(Conn, T0, Sample(10)), AlertAt(T0)]);
        using (var raw = Raw())
            raw.Execute("DROP TABLE activity_log; PRAGMA user_version = 3;");
        Assert.Empty(ReadAll());   // a schema 3 file has no log to read

        using (var store = OpenStore())
        {
            Assert.Single(store.ReadSamples(Conn.Id, T0, T0.AddSeconds(1)));
            store.Write([new ActivityRecord(Entry(ActivityEvents.AppStarted, T0))]);
        }

        using var check = Raw();
        Assert.Equal(HistoryStore.SchemaVersion, check.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(1L, check.ExecuteScalar<long>("SELECT COUNT(*) FROM alerts;"));
        Assert.Equal(1L, check.ExecuteScalar<long>("SELECT COUNT(*) FROM activity_log;"));
    }

    [Fact]
    public void EachEvent_IsWrittenWithItsSourceConnectionAndValues()
    {
        using (var store = OpenStore())
            store.Write(
            [
                new ActivityRecord(Entry(ActivityEvents.MonitoringStarted, T0, ActivitySources.LiveMetrics, ConnId, "Prod")),
                new ActivityRecord(Entry(ActivityEvents.SettingChanged, T0.AddMinutes(1)) with
                {
                    Setting = "Start monitoring automatically", OldValue = "Off", NewValue = "On",
                }),
                new ActivityRecord(Entry(ActivityEvents.MonitoringStopped, T0.AddMinutes(2), ActivitySources.SettingChanged, ConnId, "Prod") with
                {
                    Detail = "Start monitoring automatically is off.",
                }),
            ]);

        var rows = ReadAll();   // newest first

        Assert.Equal(new[] { ActivityEvents.MonitoringStopped, ActivityEvents.SettingChanged, ActivityEvents.MonitoringStarted },
                     rows.Select(r => r.Event));
        var started = rows[2];
        Assert.Equal((T0, (Guid?)ConnId, "Prod", ActivitySources.LiveMetrics), (started.TimeUtc, started.ConnectionId, started.ConnectionName, started.Source));
        Assert.Equal((@"CONTOSO\dba", "PC01"), (started.WindowsUser, started.Machine));
        Assert.Equal(DateTimeKind.Utc, started.TimeUtc.Kind);
        var setting = rows[1];
        Assert.Equal(("Start monitoring automatically", "Off", "On"), (setting.Setting, setting.OldValue, setting.NewValue));
        Assert.Null(setting.ConnectionId);
        Assert.Null(setting.ConnectionName);
        Assert.Equal("Start monitoring automatically is off.", rows[0].Detail);

        // Not tied to the connections table: the name is copied into the row.
        using var raw = Raw();
        Assert.Equal(0L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM connections;"));
    }

    [Fact]
    public void ARow_NeverKeepsASecret_ItsTextEchoed()
    {
        using (var store = OpenStore())
            store.Write(
            [
                new ActivityRecord(Entry(ActivityEvents.SettingChanged, T0) with
                {
                    Setting = "Collector history from another PC", OldValue = "Password=old pass;", NewValue = "Pwd=new;",
                    Detail  = "Posting to https://hooks.slack.com/services/T1/B2/abc failed",
                }),
            ]);

        var row = Assert.Single(ReadAll());

        Assert.Equal(("Password=*****;", "Pwd=*****;"), (row.OldValue, row.NewValue));
        Assert.Equal("Posting to [webhook URL removed] failed", row.Detail);
    }

    [Fact]
    public async Task ClearQueryTexts_ThroughTheWriter_KeepsTheRestAndLogsIt()
    {
        using (var writer = CreateWriter())
        {
            // Recent: older than the retention is purged.
            writer.Enqueue(Detail(DateTime.UtcNow, new Models.QueryTextInfo("0xAB", "Sales", "SELECT 1")));
            var clears = writer.ClearCount;

            await writer.ClearQueryTextsAsync(new ActivityRecord(Entry(ActivityEvents.QueryTextCleared, T0.AddMinutes(1))));

            Assert.Equal(clears + 1, writer.ClearCount);
        }

        Assert.Equal(ActivityEvents.QueryTextCleared, Assert.Single(ReadAll()).Event);
        using var raw = Raw();
        Assert.Equal(0L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM query_texts;"));
        Assert.Equal(1L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM detail_snapshots;"));
        Assert.Empty(_errors);
    }

    [Fact]
    public void ReadActivity_FiltersByConnectionEventAndTime()
    {
        var other = Guid.NewGuid();
        using (var store = OpenStore())
            store.Write(
            [
                new ActivityRecord(Entry(ActivityEvents.MonitoringStarted, T0, connectionId: ConnId, connectionName: "Prod")),
                new ActivityRecord(Entry(ActivityEvents.MonitoringStopped, T0.AddHours(1), connectionId: ConnId, connectionName: "Prod")),
                new ActivityRecord(Entry(ActivityEvents.MonitoringStarted, T0.AddHours(2), connectionId: other, connectionName: "Test")),
                new ActivityRecord(Entry(ActivityEvents.AppExited, T0.AddDays(-3))),
            ]);
        var reader = new HistoryReader(DbPath);

        Assert.Equal(2, reader.ReadActivity(T0.AddDays(-1), T0.AddDays(1), ConnId).Count);
        Assert.Equal(2, reader.ReadActivity(T0.AddDays(-1), T0.AddDays(1), evt: ActivityEvents.MonitoringStarted).Count);
        Assert.Equal("Test", Assert.Single(reader.ReadActivity(T0.AddDays(-1), T0.AddDays(1), other)).Entry.ConnectionName);
        Assert.Equal(3, reader.ReadActivity(T0.AddDays(-1), T0.AddDays(1)).Count);
    }

    [Fact]
    public void AStartWithoutAnExitBeforeIt_SaysThePreviousRunDidNotExitCleanly()
    {
        using (var store = OpenStore())
        {
            store.Write([new ActivityRecord(Entry(ActivityEvents.AppStarted, T0) with { Detail = "SqlVitals 0.46.0" })]);
            store.Write([new ActivityRecord(Entry(ActivityEvents.AppExited, T0.AddHours(1)))]);
            store.Write([new ActivityRecord(Entry(ActivityEvents.AppStarted, T0.AddHours(2)))]);   // the run after a clean exit
            // Killed: no App exited.
            store.Write([new ActivityRecord(Entry(ActivityEvents.MonitoringStarted, T0.AddHours(3), connectionId: ConnId))]);
            store.Write([new ActivityRecord(Entry(ActivityEvents.AppStarted, T0.AddHours(4)) with { Detail = "SqlVitals 0.46.0" })]);
        }

        var starts = ReadAll().Where(r => r.Event == ActivityEvents.AppStarted).ToList();   // newest first

        Assert.Equal($"{ActivityEvents.PreviousRunDidNotExit} SqlVitals 0.46.0", starts[0].Detail);
        Assert.Null(starts[1].Detail);
        Assert.Equal("SqlVitals 0.46.0", starts[2].Detail);   // the very first run: nothing before it
    }

    [Fact]
    public void Purge_RemovesRowsOlderThan365Days_AndKeepsNewerOnes_WhateverTheHistoryRetention()
    {
        using (var store = OpenStore())
        {
            store.Write(
            [
                new ActivityRecord(Entry(ActivityEvents.AppStarted, T0.AddDays(-400))),
                new ActivityRecord(Entry(ActivityEvents.AppStarted, T0.AddDays(-300))),
                new ActivityRecord(Entry(ActivityEvents.AppStarted, T0.AddDays(-20))),   // older than the history's 14 days
            ]);

            store.Purge(T0.AddDays(-14), T0 - ActivityLog.Retention);
        }

        Assert.Equal(new[] { T0.AddDays(-20), T0.AddDays(-300) }, ReadAll().Select(r => r.TimeUtc));
    }

    [Fact]
    public void Purge_WithoutAnActivityCutoff_KeepsTheLog()
    {
        using (var store = OpenStore())
        {
            store.Write([new ActivityRecord(Entry(ActivityEvents.AppStarted, T0.AddDays(-400)))]);
            store.Purge(T0.AddDays(-14));
        }

        Assert.Single(ReadAll());
    }

    [Fact]
    public void Writer_PurgesTheLogAfter365Days()
    {
        using (var store = OpenStore())
            store.Write([new ActivityRecord(Entry(ActivityEvents.AppStarted, T0.AddDays(-366))),
                         new ActivityRecord(Entry(ActivityEvents.AppStarted, T0.AddDays(-364)))]);

        using (var writer = CreateWriter(new ManualTime(new DateTimeOffset(T0))))
            writer.Enqueue(new ActivityRecord(Entry(ActivityEvents.AppExited, T0)));

        Assert.Equal(new[] { T0, T0.AddDays(-364) }, ReadAll().Select(r => r.TimeUtc));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ClearHistory_KeepsTheLogAndAddsAHistoryClearedRow()
    {
        using (var store = OpenStore())
            store.Write([new MetricSampleRecord(Conn, T0, Sample(1)), AlertAt(T0),
                         new ActivityRecord(Entry(ActivityEvents.MonitoringStarted, T0, connectionId: ConnId, connectionName: "Prod"))]);

        using (var writer = CreateWriter())
            await writer.ClearAsync(new ActivityRecord(Entry(ActivityEvents.HistoryCleared, T0.AddMinutes(5))));

        using var raw = Raw();
        Assert.Equal(0L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM metric_samples;"));
        Assert.Equal(0L, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM alerts;"));
        Assert.Equal(new[] { ActivityEvents.HistoryCleared, ActivityEvents.MonitoringStarted }, ReadAll().Select(r => r.Event));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ClearHistory_WithNothingSavedYet_StillLogsIt()
    {
        using (var writer = CreateWriter())
            await writer.ClearAsync(new ActivityRecord(Entry(ActivityEvents.HistoryCleared, T0)));

        Assert.Equal(ActivityEvents.HistoryCleared, Assert.Single(ReadAll()).Event);
    }

    [Fact]
    public void NoPasswordConnectionStringOrWebhookUrl_EverReachesTheTable()
    {
        // Every kind of change a save can make, through the diff, the writer and SQLite.
        var rows = SettingsDiff.Between(SettingsDiffTests.Snapshot(withConnection: false), SettingsDiffTests.Snapshot(), ActivitySources.Settings, T0)
            .Concat(SettingsDiff.Between(SettingsDiffTests.Snapshot(),
                                         SettingsDiffTests.Snapshot(automatic: true, password: "an0ther", parameters: "Password=x;Server=y", webhook: "https://h/2"),
                                         ActivitySources.Settings, T0))
            .Concat(SettingsDiff.Between(SettingsDiffTests.Snapshot(), SettingsDiffTests.Snapshot(withConnection: false), ActivitySources.Settings, T0))
            .ToList();

        using (var writer = CreateWriter())
            foreach (var row in rows)
                writer.Enqueue(new ActivityRecord(row));

        using var raw = Raw();
        Assert.Equal(rows.Count, raw.ExecuteScalar<long>("SELECT COUNT(*) FROM activity_log;"));
        var everything = string.Join("\n", raw.Query("SELECT * FROM activity_log;")
            .SelectMany(r => ((IDictionary<string, object?>)r).Values).Select(v => v?.ToString()));
        foreach (var secret in new[] { SettingsDiffTests.Password, "an0ther", "Server=sql01", "Password=x", "hooks.example.com", "https://h/2" })
            Assert.DoesNotContain(secret, everything);
    }

    [Fact]
    public void AnActivityThatCantBeSaved_GoesToTheFallback()
    {
        // A file where the folder should be: the store can never be created.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "");
        var entry = Entry(ActivityEvents.MonitoringStopped, T0, connectionId: ConnId, connectionName: "Prod");

        using (var writer = new HistoryWriter(Path.Combine(blocker, "history.db"), (m, _) => _errors.Enqueue(m),
                                              onActivityNotSaved: _notSaved.Enqueue) { DisposeWait = HistoryWriterTests.DisposeWait })
        {
            writer.Enqueue(new MetricSampleRecord(Conn, T0, Sample(1)));   // just lost: not an activity
            writer.Enqueue(new ActivityRecord(entry));
        }

        Assert.Equal(entry, Assert.Single(_notSaved));
    }

    [Fact]
    public void AnActivityQueuedAfterDispose_GoesToTheFallback()
    {
        var writer = CreateWriter();
        writer.Dispose();

        writer.Enqueue(new ActivityRecord(Entry(ActivityEvents.AppExited, T0)));

        Assert.Single(_notSaved);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
