using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Tests.History;

public sealed class HistoryBaselineTests : IDisposable
{
    private static readonly HistoryConnection Conn = HistoryStoreTests.Conn;
    private static readonly DateTime Now = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "history.db");

    private sealed record Point(double Value);

    [Fact]
    public void ReadWindow_IsTheSameRangeAWeekEarlierWithAnHourEitherSide()
    {
        var (from, to) = HistoryBaseline.ReadWindow(Now.AddHours(-1), Now);

        Assert.Equal(Now.AddDays(-7).AddHours(-2), from);
        Assert.Equal(Now.AddDays(-7).AddHours(1), to);
        Assert.Equal(DateTimeKind.Utc, from.Kind);
        Assert.Equal(DateTimeKind.Utc, to.Kind);
    }

    [Fact]
    public void Shift_MovesLastWeekOntoThisWeeksAxisAndKeepsOnlyWhatTheChartShows()
    {
        var chartFrom = new DateTime(2026, 9, 24, 8, 0, 0);
        var chartTo   = new DateTime(2026, 9, 24, 9, 0, 0);
        var lastWeek  = new (DateTime, Point?)[]
        {
            (chartFrom.AddDays(-7).AddMinutes(-30), new Point(1)),   // the read's spare hour: dropped
            (chartFrom.AddDays(-7),                 new Point(2)),
            (chartFrom.AddDays(-7).AddMinutes(30),  new Point(3)),
            (chartTo.AddDays(-7),                   new Point(4)),
            (chartTo.AddDays(-7).AddMinutes(10),    new Point(5)),   // dropped
        };

        var shifted = HistoryBaseline.Shift(lastWeek, chartFrom, chartTo).ToList();

        Assert.Equal(new[] { chartFrom, chartFrom.AddMinutes(30), chartTo }, shifted.Select(p => p.Time));
        Assert.Equal(new[] { 2.0, 3.0, 4.0 }, shifted.Select(p => p.Item!.Value));
    }

    [Fact]
    public void Shift_KeepsLastWeeksGapsAndTheClocksKind()
    {
        var start    = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Local);
        var lastWeek = new (DateTime, Point?)[]
        {
            (start,               new Point(1)),
            (start.AddMinutes(5), null),            // SqlVitals was closed
            (start.AddHours(2),   new Point(2)),
        };

        var shifted = HistoryBaseline.Shift(lastWeek, start.AddDays(7), start.AddDays(7).AddHours(3)).ToList();

        Assert.Equal(3, shifted.Count);
        Assert.Null(shifted[1].Item);
        Assert.All(shifted, p => Assert.Equal(DateTimeKind.Local, p.Time.Kind));
    }

    [Fact]
    public void Baseline_ComparesTheSameWallClockTimeAcrossADaylightSavingChange()
    {
        // London leaves summer time on Sunday 25 October 2026. Monday 09:00 the week before was
        // 08:00 UTC; Monday 09:00 after it is 09:00 UTC. The baseline for 09:00–10:00 must be
        // last Monday's 09:00–10:00 local, not the hour 168 hours earlier.
        var london    = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var lastWeek9 = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
        var chartFrom = new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
        var chartTo   = chartFrom.AddHours(1);

        using (var store = new HistoryStore(DbPath))
        {
            store.Open();
            store.Write(Enumerable.Range(0, 6)
                .Select(i => (HistoryRecord)new MetricSampleRecord(Conn, lastWeek9.AddMinutes(10 * i), HistoryStoreTests.Sample(i)))
                .ToList());
        }

        var (readFrom, readTo) = HistoryBaseline.ReadWindow(chartFrom, chartTo);
        var points = new HistoryReader(DbPath).ReadMetrics(Conn.Id, readFrom, readTo, TimeSpan.FromSeconds(10));
        var local  = points.Select(p => (TimeZoneInfo.ConvertTimeFromUtc(p.TimeUtc, london), (MetricHistoryPoint?)p));
        var shifted = HistoryBaseline.Shift(local,
            TimeZoneInfo.ConvertTimeFromUtc(chartFrom, london),
            TimeZoneInfo.ConvertTimeFromUtc(chartTo, london)).ToList();

        Assert.Equal(6, shifted.Count);
        Assert.Equal(new DateTime(2026, 10, 26, 9, 0, 0), shifted[0].Time);
        Assert.Equal(0.0, shifted[0].Item!.Averages.SqlCpuPct);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
