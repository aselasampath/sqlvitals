using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Tests.History;

public class HistoryDetailTrackerTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 6, 0, 0);
    private static readonly DateTime T0    = new(2026, 9, 23, 9, 0, 0);
    private static readonly DateTime T1    = T0.AddMinutes(5);
    private static readonly DateTime T2    = T1.AddMinutes(5);

    private static HistoryDetailSnapshot Snap(
        DateTime time,
        IReadOnlyList<WaitTypeTotals>? waits   = null,
        IReadOnlyList<FileIoTotals>?   files   = null,
        IReadOnlyList<QueryTotals>?    queries = null,
        DateTime? serverStart = null,
        HistoryMemory? memory = null) =>
        new(time, serverStart ?? Start, waits ?? [], files ?? [], queries ?? [], memory);

    private static WaitTypeTotals Wait(string type, long tasks, long ms, long signalMs = 0) => new(type, tasks, ms, signalMs);

    private static FileIoTotals File(string db, int id, long reads, long writes, long readStall = 0) =>
        new(db, id, $"{db}_{id}", "ROWS", reads, reads * 8192, readStall, writes, writes * 8192, 0);

    private static QueryTotals Query(string hash, long execs, long cpuUs, DateTime created, long reads = 0) =>
        new(hash, execs, cpuUs, cpuUs * 2, reads, 0, created);

    [Fact]
    public void Add_FirstSnapshotIsTheBaselineAndReturnsNothing()
    {
        var tracker = new HistoryDetailTracker();
        Assert.Null(tracker.QueriesExecutedSince);

        Assert.Null(tracker.Add(Snap(T0, waits: [Wait("PAGEIOLATCH_SH", 10, 100)]), topQueries: 20));
        Assert.Equal(T0, tracker.QueriesExecutedSince);
    }

    [Fact]
    public void Add_WaitsAreTheChangeSinceThePreviousSnapshot()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, waits: [Wait("PAGEIOLATCH_SH", 10, 100, 5), Wait("LCK_M_X", 3, 900)]), 20);

        var detail = tracker.Add(Snap(T1, waits:
        [
            Wait("PAGEIOLATCH_SH", 15, 250, 7),
            Wait("LCK_M_X", 3, 900),          // unchanged: not stored
            Wait("WRITELOG", 4, 40),          // new this interval: previous total was zero
        ]), 20)!;

        Assert.Equal(300, detail.IntervalSeconds);
        Assert.Equal(T1, detail.ServerTime);
        Assert.Equal(
            new[] { Wait("PAGEIOLATCH_SH", 5, 150, 2), Wait("WRITELOG", 4, 40) },
            detail.Waits);
    }

    [Fact]
    public void Add_SkipsWaitsThatWentBackwards()
    {
        // DBCC SQLPERF('sys.dm_os_wait_stats', CLEAR) resets totals mid-interval.
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, waits: [Wait("CXPACKET", 100, 10_000)]), 20);

        var detail = tracker.Add(Snap(T1, waits: [Wait("CXPACKET", 5, 50)]), 20)!;

        Assert.Empty(detail.Waits);
    }

    [Fact]
    public void Add_FileIoIsTheChangeAndSkipsIdleFiles()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, files: [File("Sales", 1, 100, 10), File("Sales", 2, 0, 50)]), 20);

        var detail = tracker.Add(Snap(T1, files:
        [
            File("Sales", 1, 160, 10, readStall: 30),
            File("Sales", 2, 0, 50),          // idle
            File("Archive", 1, 7, 0),         // came online in the interval
        ]), 20)!;

        Assert.Equal(2, detail.Files.Count);
        var sales = detail.Files[0];
        Assert.Equal(("Sales", 1, 60L, 60L * 8192, 30L, 0L), (sales.DatabaseName, sales.FileId, sales.Reads, sales.BytesRead, sales.ReadStallMs, sales.Writes));
        Assert.Equal(("Archive", 7L), (detail.Files[1].DatabaseName, detail.Files[1].Reads));
    }

    [Fact]
    public void Add_ServerRestartStartsANewBaseline()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, waits: [Wait("WRITELOG", 1000, 5000)]), 20);

        var restarted = Snap(T1, waits: [Wait("WRITELOG", 3, 20)], serverStart: T1.AddMinutes(-1));
        Assert.Null(tracker.Add(restarted, 20));

        var detail = tracker.Add(Snap(T2, waits: [Wait("WRITELOG", 8, 70)], serverStart: T1.AddMinutes(-1)), 20)!;
        Assert.Equal(new[] { Wait("WRITELOG", 5, 50) }, detail.Waits);
    }

    [Fact]
    public void Add_ClockGoingBackwardsStartsANewBaseline()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T1), 20);

        Assert.Null(tracker.Add(Snap(T0), 20));
    }

    [Fact]
    public void Add_QueryInTheBaselineIsDiffedAgainstIt()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, queries: [Query("0xA", 100, 1_000_000, Start)]), 20);

        var detail = tracker.Add(Snap(T1, queries: [Query("0xA", 130, 1_600_000, Start, reads: 90)]), 20)!;

        Assert.Equal(new QueryDelta("0xA", 30, 600_000, 1_200_000, 90, 0), Assert.Single(detail.TopQueries));
    }

    [Fact]
    public void Add_BaselineCarriesQueriesThatSkippedAnInterval()
    {
        // After the baseline only queries that ran are read, so 0xA is missing at T1. Its
        // baseline totals from T0 must still be there when it runs again at T2.
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, queries: [Query("0xA", 100, 1_000_000, Start)]), 20);
        tracker.Add(Snap(T1), 20);

        var detail = tracker.Add(Snap(T2, queries: [Query("0xA", 101, 1_900_000, Start)]), 20)!;

        Assert.Equal(900_000, Assert.Single(detail.TopQueries).WorkerTimeUs);
    }

    [Fact]
    public void Add_QueryCompiledInTheIntervalCountsInFull()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0), 20);

        var detail = tracker.Add(Snap(T1, queries: [Query("0xNEW", 4, 80_000, T0.AddMinutes(2))]), 20)!;

        Assert.Equal(new QueryDelta("0xNEW", 4, 80_000, 160_000, 0, 0), Assert.Single(detail.TopQueries));
    }

    [Fact]
    public void Add_OldQueryMissingFromTheBaselineIsSkipped()
    {
        // Its plan predates the interval but its earlier totals were never read: any figure
        // would overstate the interval.
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0), 20);

        var detail = tracker.Add(Snap(T1, queries: [Query("0xOLD", 5_000, 90_000_000, Start)]), 20)!;

        Assert.Empty(detail.TopQueries);
    }

    [Fact]
    public void Add_EvictedAndRecompiledPlanCountsInFullWhenAllPlansAreNew()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, queries: [Query("0xA", 1_000, 50_000_000, Start)]), 20);

        var detail = tracker.Add(Snap(T1, queries: [Query("0xA", 3, 9_000, T0.AddMinutes(1))]), 20)!;

        Assert.Equal(new QueryDelta("0xA", 3, 9_000, 18_000, 0, 0), Assert.Single(detail.TopQueries));
    }

    [Fact]
    public void Add_EvictedPlanWithAnOlderSurvivorIsSkipped()
    {
        // One of two plans left the cache: the sum dropped, and the survivor is older than the
        // interval, so the interval's work can't be separated out.
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, queries: [Query("0xA", 1_000, 50_000_000, Start)]), 20);

        var detail = tracker.Add(Snap(T1, queries: [Query("0xA", 600, 20_000_000, Start)]), 20)!;

        Assert.Empty(detail.TopQueries);
    }

    [Fact]
    public void Add_KeepsTheTopQueriesByCpuInTheInterval()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0, queries:
        [
            Query("0xBIG_TOTAL", 1_000, 900_000_000, Start),
            Query("0xSPIKE",     10,    1_000,       Start),
            Query("0xIDLE",      50,    5_000,       Start),
        ]), 20);

        var detail = tracker.Add(Snap(T1, queries:
        [
            Query("0xBIG_TOTAL", 1_001, 900_100_000, Start),   // +100 ms
            Query("0xSPIKE",     60,    40_001_000,  Start),   // +40 s
            Query("0xIDLE",      50,    5_000,       Start),   // did not run
        ]), topQueries: 2)!;

        Assert.Equal(new[] { "0xSPIKE", "0xBIG_TOTAL" }, detail.TopQueries.Select(q => q.QueryHash));
    }

    [Fact]
    public void Add_PassesMemoryThrough()
    {
        var memory  = new HistoryMemory(8_000_000, 16_000_000, 6_000_000, 500_000, 20_000, 100_000, 2);
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0), 20);

        Assert.Equal(memory, tracker.Add(Snap(T1, memory: memory), 20)!.Memory);
    }

    private static HistoryDetailSnapshot WithCounters(DateTime time, params CounterTotals[] counters) =>
        Snap(time) with { Counters = counters };

    [Fact]
    public void Add_RateCountersBecomePerSecondAndOthersAreTakenAsTheyAre()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(WithCounters(T0,
            new("Batch Requests/sec", 10_000, IsRate: true),
            new("Page life expectancy", 800, IsRate: false)), 20);

        var detail = tracker.Add(WithCounters(T1,
            new("Batch Requests/sec", 40_000, IsRate: true),     // +30,000 over 300 s
            new("Page life expectancy", 1_100, IsRate: false)), 20)!;

        Assert.Equal(
            new[] { new CounterValue("Batch Requests/sec", 100), new CounterValue("Page life expectancy", 1_100) },
            detail.Counters);
    }

    [Fact]
    public void Add_LeavesOutZerosAndRatesWithNoTrueFigure()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(WithCounters(T0,
            new("Number of Deadlocks/sec", 3, IsRate: true),
            new("Lock Waits/sec", 500, IsRate: true)), 20);

        var detail = tracker.Add(WithCounters(T1,
            new("Number of Deadlocks/sec", 3, IsRate: true),     // none in the interval
            new("Lock Waits/sec", 20, IsRate: true),             // went backwards
            new("Page Splits/sec", 70, IsRate: true),            // not in the previous read
            new("Processes blocked", 0, IsRate: false)), 20)!;

        Assert.Empty(detail.Counters!);
    }

    [Fact]
    public void Add_TakesTheFirstRowOfACounterName()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(WithCounters(T0), 20);

        var detail = tracker.Add(WithCounters(T1,
            new("Lock waits", 12, IsRate: false),
            new("Lock waits", 999, IsRate: false)), 20)!;

        Assert.Equal(new[] { new CounterValue("Lock waits", 12) }, detail.Counters);
    }

    [Fact]
    public void Add_SnapshotsWithoutCountersGiveNone()
    {
        var tracker = new HistoryDetailTracker();
        tracker.Add(Snap(T0), 20);

        Assert.Empty(tracker.Add(Snap(T1), 20)!.Counters!);
    }
}
