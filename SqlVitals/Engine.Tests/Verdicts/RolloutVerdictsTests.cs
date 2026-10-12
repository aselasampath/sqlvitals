using System.Globalization;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Security;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Engine.Tests.Verdicts;

/// <summary>
/// The verdicts of the Resources, Maintenance, Configuration and global pages (#150): what is wrong,
/// or that nothing is, including when there is nothing to judge and when the page couldn't tell.
/// </summary>
public class RolloutVerdictsTests
{
    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static readonly DateTime Now = new(2026, 10, 6, 10, 0, 0);

    // ── Memory Grants ────────────────────────────────────────────────────

    private static MemorySnapshot Snapshot(string state = "Available physical memory is high", long pending = 0) =>
        new(16 * 1024 * 1024, 12 * 1024 * 1024, 14 * 1024 * 1024, 9 * 1024 * 1024, 1024 * 1024, 512 * 1024, 0, pending, 0,
            3 * 1024 * 1024, 20 * 1024 * 1024, 10 * 1024 * 1024, state, "CONVENTIONAL", Now);

    private static MemoryGrant Grant(int spid, bool pending, int waitMs = 0, long requestedKb = 2 * 1024 * 1024, long grantedKb = 0) =>
        new(spid, 0, 1, 4, Now, pending ? null : Now, waitMs, requestedKb, grantedKb, 0, 0, null, requestedKb, 1024, null, null, false, waitMs,
            "app", "host", "OrderService", "SalesDb", "suspended", pending ? "RESOURCE_SEMAPHORE" : null, "SELECT 1", Now, pending, "Info");

    [Fact]
    public void Memory_NothingWaiting_SaysHowMemoryIsUsed()
    {
        var verdict = Invariant(() => MemoryVerdict.From(Snapshot(), []));
        Assert.Equal((VerdictLevel.Ok, "No query waits for memory"), (verdict.Level, verdict.Headline));
        Assert.Equal("SQL Server uses 12 GB of its 14 GB target; the buffer pool 9 GB, the plan cache 1 GB. No query holds a memory grant now.", verdict.Why);
        Assert.Equal([new VerdictAction(MemoryVerdict.Clerks, "Memory clerks")], verdict.Actions);

        var running = Invariant(() => MemoryVerdict.From(Snapshot(), [Grant(51, false, grantedKb: 512 * 1024)]));
        Assert.EndsWith(" 1 query holds 512 MB of grants.", running.Why);
    }

    [Fact]
    public void Memory_GrantsWaiting_NameTheLongest_CriticalPastThirtySeconds()
    {
        var verdict = Invariant(() => MemoryVerdict.From(Snapshot(), [Grant(51, true, 4_000), Grant(52, true, 42_000), Grant(60, false, grantedKb: 1024)]));
        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.Equal("2 queries wait for a memory grant; session 52 the longest, for 42 s", verdict.Headline);
        Assert.StartsWith("It asked for 2 GB (RESOURCE_SEMAPHORE).", verdict.Why);
        Assert.Equal([new VerdictAction(MemoryVerdict.ShowWaiting, "Show the waiting grants")], verdict.Actions);

        var brief = Invariant(() => MemoryVerdict.From(Snapshot(), [Grant(51, true, 1_500)]));
        Assert.Equal((VerdictLevel.Warning, "1 query waits for a memory grant; session 51 the longest, for 1.5 s"), (brief.Level, brief.Headline));
    }

    [Fact]
    public void Memory_ThePendingCounter_SaysWhetherQueriesWait_TheListOnlyNamesThem()
    {
        // Waiting, but not among the 25 largest grants listed.
        var unlisted = Invariant(() => MemoryVerdict.From(Snapshot(pending: 3), [Grant(51, false, grantedKb: 1024)]));
        Assert.Equal((VerdictLevel.Warning, "3 queries wait for a memory grant"), (unlisted.Level, unlisted.Headline));
        Assert.StartsWith("They aren't among the 25 largest grants listed.", unlisted.Why);

        // A trend reading: only the counter was read.
        var reading = Invariant(() => MemoryVerdict.From(Snapshot(pending: 1), null));
        Assert.Equal("1 query waits for a memory grant", reading.Headline);
        Assert.StartsWith("Refresh lists them.", reading.Why);
        var calm = Invariant(() => MemoryVerdict.From(Snapshot(), null));
        Assert.Equal("SQL Server uses 12 GB of its 14 GB target; the buffer pool 9 GB, the plan cache 1 GB.", calm.Why);
    }

    [Fact]
    public void Memory_WindowsLow_IsCritical()
    {
        var verdict = Invariant(() => MemoryVerdict.From(Snapshot("Available physical memory is low"), []));
        Assert.Equal((VerdictLevel.Critical, "Windows reports low physical memory"), (verdict.Level, verdict.Headline));
        Assert.StartsWith("3 GB of 16 GB is available (Available physical memory is low).", verdict.Why);
    }

    // ── TempDB ───────────────────────────────────────────────────────────

    private static TempDbFile File(string name, long size, long used, string type = "ROWS") =>
        new(name, type, $@"T:\{name}.ndf", size, used, size - used, 100.0 * used / size, "64 MB", Now);

    private static TempDbSession Session(int spid, long netKb, long internalKb = 0) =>
        new(spid, netKb - internalKb, 0, internalKb, 0, netKb, "etl", "host", "ETL", null, "running", 10, "SalesDb", "SELECT 1", Now, "Info");

    private static TempDbCounter Counter(string name, long value) => new(name, value, Now);

    [Fact]
    public void TempDb_Room_SaysHowMuchIsFree_AndWhoHoldsTheMost()
    {
        var verdict = Invariant(() => TempDbVerdict.From(
            [File("tempdev", 8_192, 1_024), File("temp2", 8_192, 1_024), File("templog", 2_048, 1_900, "LOG")],
            [Session(51, 20_480), Session(52, 102_400, internalKb: 90_000)],
            [Counter("Version Store Size (KB)", 2_048)]));

        Assert.Equal((VerdictLevel.Ok, "TempDB has room: 14 GB free of 16 GB"), (verdict.Level, verdict.Headline));
        Assert.Equal("Session 52 (etl) holds the most, 100 MB: sorts and hashes spilling. The version store holds 2 MB.", verdict.Why);
        Assert.Equal([new VerdictAction(TempDbVerdict.ShowSession, "Show the session")], verdict.Actions);
    }

    [Fact]
    public void TempDb_ABigSession_FullFiles_AndABigVersionStore_AreWarnings()
    {
        var session = Invariant(() => TempDbVerdict.From([File("tempdev", 8_192, 1_024)], [Session(51, 800 * 1024)], []));
        Assert.Equal((VerdictLevel.Warning, "Session 51 holds 800 MB of TempDB"), (session.Level, session.Headline));

        var full = Invariant(() => TempDbVerdict.From([File("tempdev", 1_000, 950)], [], []));
        Assert.Equal((VerdictLevel.Warning, "TempDB's data files are 95 % used: 50 MB free of 1,000 MB"), (full.Level, full.Headline));
        Assert.Empty(full.Actions);

        var versions = Invariant(() => TempDbVerdict.From([File("tempdev", 8_192, 3_000)], [], [Counter("Version Store Size (KB)", 2 * 1024 * 1024)]));
        Assert.Equal((VerdictLevel.Warning, "The version store holds 2 GB"), (versions.Level, versions.Headline));
    }

    [Fact]
    public void TempDb_NothingRead_IsUnknown() =>
        Assert.Equal(VerdictLevel.Unknown, TempDbVerdict.From([], [], []).Level);

    // ── File I/O ─────────────────────────────────────────────────────────

    private static FileIoCounters Counters(int db, string name, int fileId, string type, long reads, long readStall, long writes, long writeStall, string volume = @"D:\") =>
        new(db, name, fileId, $"{name}_{type.ToLowerInvariant()}", type, $@"{volume}{name}.mdf", volume, reads, reads * 8192, readStall, writes, writes * 8192, writeStall, 1L << 30);

    private static FileLatencyList Latencies(params (FileIoCounters Before, FileIoCounters After)[] files) =>
        FileLatencyList.Between(new FileIoReading(Now, Now.AddDays(-1), files.Select(f => f.Before).ToList()),
                                new FileIoReading(Now.AddSeconds(10), Now.AddDays(-1), files.Select(f => f.After).ToList()),
                                FileLatencyThresholds.Default);

    [Fact]
    public void FileIo_BeforeAndWhileMeasuring_AndUnreadable()
    {
        Assert.Equal((VerdictLevel.Unknown, "Reading the file I/O totals…"), (FileIoVerdict.From(null).Level, FileIoVerdict.From(null).Headline));

        var first = FileLatencyList.Between(null, new FileIoReading(Now, Now.AddDays(-1), [Counters(5, "Sales", 1, "ROWS", 10, 10, 0, 0)]), FileLatencyThresholds.Default);
        Assert.Equal((VerdictLevel.Info, "Measuring: latency needs a second reading"), (FileIoVerdict.From(first).Level, FileIoVerdict.From(first).Headline));

        var denied = FileIoVerdict.From(FileLatencyList.Unavailable("Reading it needs VIEW SERVER STATE.", FileLatencyThresholds.Default));
        Assert.Equal((VerdictLevel.Unknown, "Couldn't read the file I/O", "Reading it needs VIEW SERVER STATE."), (denied.Level, denied.Headline, denied.Why));
    }

    [Fact]
    public void FileIo_NoneSlow_IsOk()
    {
        var verdict = Invariant(() => FileIoVerdict.From(Latencies(
            (Counters(5, "Sales", 1, "ROWS", 100, 100, 0, 0), Counters(5, "Sales", 1, "ROWS", 200, 600, 0, 0)))));
        Assert.Equal((VerdictLevel.Ok, "No file is slower than your threshold"), (verdict.Level, verdict.Headline));
        Assert.Equal("1 file over 10 s, highlighted for data files over 20 ms, log files over 10 ms. The slowest, Sales · Sales_rows, took 5.0 ms.", verdict.Why);
        Assert.Empty(verdict.Actions);
    }

    [Fact]
    public void FileIo_SlowFiles_TheWorstFirst_WithTheVolume()
    {
        var verdict = Invariant(() => FileIoVerdict.From(Latencies(
            (Counters(5, "Sales", 1, "ROWS", 100, 100, 0, 0), Counters(5, "Sales", 1, "ROWS", 200, 4_100, 0, 0)),          // reads 40 ms
            (Counters(5, "Sales", 2, "LOG", 0, 0, 100, 100), Counters(5, "Sales", 2, "LOG", 0, 0, 200, 6_100)),            // writes 60 ms: 6× its 10 ms
            (Counters(6, "Hr", 1, "ROWS", 100, 100, 0, 0, @"E:\"), Counters(6, "Hr", 1, "ROWS", 200, 300, 0, 0, @"E:\")))));   // 2 ms

        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.Equal("2 files are slower than your threshold; Sales · Sales_log writes in 60.0 ms", verdict.Headline);
        Assert.Contains("1 is at 5× the threshold or more.", verdict.Why);
        Assert.Contains(@"A volume is slow as a whole (D:\): the storage itself, not one file.", verdict.Why);
        Assert.Equal([new VerdictAction(FileIoVerdict.ShowSlow, "Show the slow files")], verdict.Actions);
    }

    // ── DB Storage ───────────────────────────────────────────────────────

    private static DatabaseFile DbFile(string db, string type, decimal sizeMb, decimal maxMb = -1, bool autoShrink = false, string logical = "") =>
        new(db, "ONLINE", "FULL", 160, null, autoShrink, true, true, false, false, false, type, logical.Length > 0 ? logical : $"{db}_{type}",
            $@"D:\{db}.mdf", sizeMb, sizeMb / 2, sizeMb / 2, maxMb, "64 MB");

    [Fact]
    public void DbStorage_NothingNearALimit_GivesTheStorageAtAGlance()
    {
        var verdict = Invariant(() => DbStorageVerdict.From([DbFile("Sales", "ROWS", 2_048), DbFile("Sales", "LOG", 512), DbFile("Hr", "ROWS", 100)],
                                                            [new("tempdev", "ROWS", @"T:\t.mdf", 1_024, 10, 1_014)]));
        Assert.Equal((VerdictLevel.Info, "2.6 GB of database files, none near a limit"), (verdict.Level, verdict.Headline));
        Assert.Equal("2.6 GB in 2 databases; the largest, Sales, 2.5 GB. Data 2.1 GB, log 512 MB, TempDB 1 GB.", verdict.Why);
        Assert.Empty(verdict.Actions);
        Assert.Null(DbStorageVerdict.Subject([DbFile("Sales", "ROWS", 2_048)]));
    }

    [Fact]
    public void DbStorage_AFileNearItsMaximum_ComesFirst_ThenAutoShrink()
    {
        List<DatabaseFile> files = [DbFile("Sales", "LOG", 950, maxMb: 1_000, logical: "Sales_log"), DbFile("Hr", "ROWS", 100, autoShrink: true)];
        var verdict = Invariant(() => DbStorageVerdict.From(files, []));
        Assert.Equal((VerdictLevel.Warning, "1 file is near its maximum size; Sales · Sales_log at 95 %"), (verdict.Level, verdict.Headline));
        Assert.StartsWith("It is 950 MB of 1,000 MB. A file at its maximum can't grow", verdict.Why);
        Assert.Equal("Sales_log", DbStorageVerdict.Subject(files)!.LogicalFileName);

        var shrink = Invariant(() => DbStorageVerdict.From([files[1]], []));
        Assert.Equal((VerdictLevel.Warning, "Auto-shrink is on for Hr"), (shrink.Level, shrink.Headline));
        Assert.Equal([new VerdictAction(DbStorageVerdict.ShowFile, "Show the file")], shrink.Actions);
    }

    [Fact]
    public void DbStorage_NothingRead_IsUnknown() =>
        Assert.Equal(VerdictLevel.Unknown, DbStorageVerdict.From([], []).Level);

    // ── Perfmon ──────────────────────────────────────────────────────────

    [Fact]
    public void Perfmon_NothingSelected_NotStarted_AndNothingInTheRange()
    {
        Assert.Equal("No counter is selected", PerfmonVerdict.From([], WaitTrendState.Live).Headline);
        PerfmonSeries[] empty = [new("Batch Requests/sec", [null, null])];
        Assert.Equal((VerdictLevel.Unknown, "Not charting yet"), (PerfmonVerdict.From(empty, WaitTrendState.NotStarted).Level, PerfmonVerdict.From(empty, WaitTrendState.NotStarted).Headline));
        Assert.Equal("Reading the first samples", PerfmonVerdict.From(empty, WaitTrendState.Live).Headline);
        Assert.Equal("Not recorded then.", PerfmonVerdict.From(empty, WaitTrendState.History, "Not recorded then.").Why);
    }

    [Fact]
    public void Perfmon_ACounterPastItsLimit_SaysWhatItMeans_AndWhereToLook()
    {
        PerfmonSeries[] series =
        [
            new("Batch Requests/sec", [100, 110, 120, 300, 310, 320]),
            new("Memory Grants Pending", [0, 0, 0, 0, 0, 3]),
            new("Processes blocked", [0, 0, 2]),
        ];
        var verdict = Invariant(() => PerfmonVerdict.From(series, WaitTrendState.Live));

        Assert.Equal((VerdictLevel.Warning, "Memory Grants Pending is 3: queries wait for a memory grant"), (verdict.Level, verdict.Headline));
        Assert.Equal("Also past its limit: Processes blocked at 2. Rising: Batch Requests/sec.", verdict.Why);
        Assert.Equal([new VerdictAction(PerfmonVerdict.Cause, "Memory")], verdict.Actions);
        Assert.Equal("Memory", PerfmonVerdict.Breached(series)!.Page);
    }

    [Fact]
    public void Perfmon_NonePast_IsOk_OrANoteWhenRising()
    {
        var calm = Invariant(() => PerfmonVerdict.From([new("Processes blocked", [0, 0, 0])], WaitTrendState.Live));
        Assert.Equal((VerdictLevel.Ok, "1 counter charted, none past a known limit", "1 of them has a well-known limit."), (calm.Level, calm.Headline, calm.Why));

        var rising = Invariant(() => PerfmonVerdict.From([new("Batch Requests/sec", [1, 1, 1, 5, 5, 5])], WaitTrendState.History));
        Assert.Equal(VerdictLevel.Info, rising.Level);
        Assert.EndsWith("Rising: Batch Requests/sec.", rising.Why);
        Assert.Null(PerfmonVerdict.Breached([new("Batch Requests/sec", [1, 1, 1, 5, 5, 5])]));
    }

    // ── Index Health ─────────────────────────────────────────────────────

    private static MissingIndex Missing(string table, double impact, string severity) =>
        new("SalesDb", "dbo", table, "[CustomerId]", null, "[Total]", 3, 1_200, 40, Now, null, 2.5, 80, impact, severity,
            $"CREATE INDEX IX_{table} ON dbo.{table} ([CustomerId]) INCLUDE ([Total]);", Now);

    private static UnusedIndex Unused(string name, long updates) =>
        new("SalesDb", "dbo", "Orders", name, "NONCLUSTERED", false, false, false, 0, 0, 0, updates, null, null, Now, 0, 1_000_000, Now);

    [Fact]
    public void IndexHealth_NothingSuggested_IsOk_UnlessIndexesGoUnused()
    {
        var verdict = IndexHealthVerdict.From([], []);
        Assert.Equal((VerdictLevel.Ok, "SQL Server suggests no missing index"), (verdict.Level, verdict.Headline));
        Assert.Empty(verdict.Actions);

        var unused = Invariant(() => IndexHealthVerdict.From([], [Unused("IX_a", 4_000), Unused("IX_b", 1_000)]));
        Assert.Equal(VerdictLevel.Info, unused.Level);
        Assert.StartsWith("2 unused indexes only cost writes: not read since the server started, updated 5,000 times.", unused.Why);
        Assert.Equal([new VerdictAction(IndexHealthVerdict.ShowUnused, "Unused indexes")], unused.Actions);
    }

    [Fact]
    public void IndexHealth_NamesTheMostValuable_CriticalFirst()
    {
        var verdict = Invariant(() => IndexHealthVerdict.From(
            [Missing("Orders", 50_000, "WARNING"), Missing("Customers", 150_000, "CRITICAL"), Missing("Lines", 900_000, "INFO")],
            [Unused("IX_a", 10)]));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("3 missing indexes suggested, 1 critical; the most valuable on SalesDb.dbo.Customers", verdict.Headline);
        Assert.StartsWith("It would serve 1,200 seeks and 40 scans (impact 150,000), on [CustomerId].", verdict.Why);
        Assert.Equal([new VerdictAction(IndexHealthVerdict.ShowMissing, "Show it"), new VerdictAction(IndexHealthVerdict.ShowUnused, "Unused indexes")], verdict.Actions);
        Assert.Equal("Customers", IndexHealthVerdict.Top([Missing("Orders", 50_000, "WARNING"), Missing("Customers", 150_000, "CRITICAL")])!.TableName);
    }

    // ── Stale Stats ──────────────────────────────────────────────────────

    private static StaleStatistic Stat(string table, long rows, long changes, int? days = 40) =>
        new("dbo", table, $"IX_{table}", days is null ? null : Now.AddDays(-days.Value), days, rows, rows, 100, changes, false, false, false, "SalesDb", Now);

    [Fact]
    public void StaleStats_NothingChanged_IsOk()
    {
        Assert.Equal((VerdictLevel.Ok, "No statistic has changes pending"), (StaleStatsVerdict.From([]).Level, StaleStatsVerdict.From([]).Headline));
        Assert.Equal("1 statistic listed, none with a row changed since its last update.", StaleStatsVerdict.From([Stat("Orders", 100, 0)]).Why);
    }

    [Fact]
    public void StaleStats_TheLargestShareChanged_First()
    {
        var verdict = Invariant(() => StaleStatsVerdict.From([Stat("Orders", 1_000_000, 50_000), Stat("Lines", 10_000, 4_000, days: 12), Stat("Notes", 100, 30)]));
        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("2 statistics have 20 % or more of their table changed; the most, SalesDb.dbo.Lines (IX_Lines)", verdict.Headline);
        Assert.StartsWith("4,000 of its 10,000 rows changed (40 %), last updated 2026-09-24, 12 days ago.", verdict.Why);
        Assert.Equal([new VerdictAction(StaleStatsVerdict.ShowWorst, "Show it")], verdict.Actions);

        var few = Invariant(() => StaleStatsVerdict.From([Stat("Orders", 1_000_000, 50_000)]));
        Assert.Equal((VerdictLevel.Info, "No statistic has 20 % of its table changed"), (few.Level, few.Headline));
    }

    // ── Agent Jobs ───────────────────────────────────────────────────────

    private static AgentJob Job(string name, JobOutcome? last = JobOutcome.Succeeded, TimeSpan? runningFor = null, bool enabled = true, string? message = null) =>
        new(Guid.NewGuid(), name, enabled, "Database Maintenance", "sa", null, last, Now.AddHours(-2), TimeSpan.FromMinutes(5), message,
            20, last == JobOutcome.Failed ? 3 : 0, 17, TimeSpan.FromMinutes(5),
            runningFor is null ? null : Now - runningFor, runningFor, Now.AddHours(22), "2026-10-07 08:00");

    [Fact]
    public void AgentJobs_Unreadable_None_AndAgentStopped()
    {
        var denied = AgentJobsVerdict.From(AgentJobList.Unavailable("SQL Agent isn't part of Azure SQL Database."));
        Assert.Equal((VerdictLevel.Unknown, "Couldn't read the SQL Agent jobs"), (denied.Level, denied.Headline));

        Assert.Equal((VerdictLevel.Ok, "There are no SQL Agent jobs on this server"),
                     (AgentJobsVerdict.From(new([], AgentServiceState.Running, Now, null)).Level, AgentJobsVerdict.From(new([], AgentServiceState.Running, Now, null)).Headline));

        var stopped = Invariant(() => AgentJobsVerdict.From(new([Job("Backup"), Job("Old", enabled: false)], AgentServiceState.Stopped, Now, null)));
        Assert.Equal((VerdictLevel.Critical, "SQL Server Agent is stopped: no job runs"), (stopped.Level, stopped.Headline));
        Assert.StartsWith("1 job is enabled, none will start", stopped.Why);
    }

    [Fact]
    public void AgentJobs_AllWell_IsOk_WithTheNextRun()
    {
        var verdict = Invariant(() => AgentJobsVerdict.From(new([Job("Backup"), Job("Index")], AgentServiceState.Running, Now, null)));
        Assert.Equal((VerdictLevel.Ok, "No job failed its last run, and none is running long"), (verdict.Level, verdict.Headline));
        Assert.Equal("2 jobs, 0 running now; next: Backup at 2026-10-07 08:00 (server time).", verdict.Why);
        Assert.Empty(verdict.Actions);
    }

    [Fact]
    public void AgentJobs_FailedAndRunningLong_AreNamed()
    {
        var list = new AgentJobList(AgentJobList.Sort([Job("Nightly backup", JobOutcome.Failed, message: "Step 2 failed: disk full."), Job("Reindex", runningFor: TimeSpan.FromMinutes(30)), Job("Purge")]),
                                    AgentServiceState.Running, Now, null);
        var verdict = Invariant(() => AgentJobsVerdict.From(list));

        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.Equal("1 job failed its last run: Nightly backup; 1 is running longer than usual: Reindex", verdict.Headline);
        Assert.Equal("Nightly backup: Last run failed at 2026-10-06 08:00:00 after 0:05:00. Step 2 failed: disk full.", verdict.Why);
        Assert.Equal("Nightly backup", AgentJobsVerdict.Subject(list)!.Name);
        Assert.Equal([new VerdictAction(AgentJobsVerdict.ShowJob, "Show its runs"), new VerdictAction(AgentJobsVerdict.NeedsAttention, "Only these")], verdict.Actions);

        var failed = Invariant(() => AgentJobsVerdict.From(new([Job("A", JobOutcome.Failed, message: "Login failed."), Job("B", JobOutcome.Failed), Job("C", JobOutcome.Failed)], AgentServiceState.Running, Now, null)));
        Assert.Equal("3 jobs failed their last run: A, B and 1 more", failed.Headline);
        Assert.EndsWith("Login failed.", failed.Why);

        var slow = Invariant(() => AgentJobsVerdict.From(new([Job("Reindex", runningFor: TimeSpan.FromMinutes(30))], AgentServiceState.Running, Now, null)));
        Assert.Equal((VerdictLevel.Warning, "1 job is running longer than usual: Reindex"), (slow.Level, slow.Headline));
    }

    // ── Config Checks ────────────────────────────────────────────────────

    private static ConfigCheck Check(ConfigCheckKind kind, ConfigCheckStatus status, string scope = ConfigCheck.ServerScope) =>
        new(kind, scope, status, "0", "8", $"Why {kind} matters.", status == ConfigCheckStatus.Warn ? $"-- fix {kind}" : null);

    [Fact]
    public void ConfigChecks_AllPass_IsOk_SayingWhatWasntChecked()
    {
        var verdict = ConfigChecksVerdict.From(new([Check(ConfigCheckKind.MaxDop, ConfigCheckStatus.Pass), Check(ConfigCheckKind.PageVerify, ConfigCheckStatus.NotChecked)], null));
        Assert.Equal((VerdictLevel.Ok, "Every setting checked follows best practice", "1 check passed. 1 setting couldn't be checked."), (verdict.Level, verdict.Headline, verdict.Why));

        var nothing = ConfigChecksVerdict.From(new([], "VIEW SERVER STATE is missing."));
        Assert.Equal((VerdictLevel.Unknown, "Nothing could be checked", "VIEW SERVER STATE is missing."), (nothing.Level, nothing.Headline, nothing.Why));
    }

    [Fact]
    public void ConfigChecks_Failing_TheRiskiestFirst()
    {
        var list = new ConfigCheckList(ConfigCheckList.Sort(
        [
            Check(ConfigCheckKind.MaxDop, ConfigCheckStatus.Warn),
            Check(ConfigCheckKind.CostThreshold, ConfigCheckStatus.Pass),
            Check(ConfigCheckKind.PageVerify, ConfigCheckStatus.Warn, "SalesDb"),
        ]), null);
        var verdict = Invariant(() => ConfigChecksVerdict.From(list));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("2 of 3 checks fail; the riskiest: Page verify (SalesDb)", verdict.Headline);
        Assert.Equal("0, recommended 8. Why PageVerify matters.", verdict.Why);
        Assert.Equal([new VerdictAction(ConfigChecksVerdict.ShowCheck, "Show it"), new VerdictAction(ConfigChecksVerdict.Script, "Script the warnings")], verdict.Actions);
        Assert.Equal(ConfigCheckKind.PageVerify, ConfigChecksVerdict.Riskiest(list)!.Kind);
    }

    // ── Security Audit ───────────────────────────────────────────────────

    private static SecurityFinding Finding(SecurityCheckKind kind, SecurityFindingStatus status, string subject) =>
        new(kind, SecurityFinding.ServerScope, subject, status, "SQL login · enabled", $"Why {subject} is a risk.", status == SecurityFindingStatus.Risk ? "-- fix" : null);

    [Fact]
    public void SecurityAudit_Unreadable_AndNothingFound()
    {
        var denied = SecurityAuditVerdict.From(SecurityFindingList.Unavailable("Reading the logins needs VIEW ANY DEFINITION."));
        Assert.Equal((VerdictLevel.Unknown, "Couldn't audit the logins and databases"), (denied.Level, denied.Headline));

        var fine = SecurityAuditVerdict.From(new([Finding(SecurityCheckKind.SaLogin, SecurityFindingStatus.Pass, "sa")], null));
        Assert.Equal((VerdictLevel.Ok, "Nothing audited widens the attack surface"), (fine.Level, fine.Headline));
    }

    [Fact]
    public void SecurityAudit_Risks_TheHighestFirst_ThenWhatToReview()
    {
        var list = new SecurityFindingList(SecurityFindingList.Sort(
        [
            Finding(SecurityCheckKind.SysadminMember, SecurityFindingStatus.Risk, "CORP\\jane"),
            Finding(SecurityCheckKind.SaLogin, SecurityFindingStatus.Risk, "sa"),
            Finding(SecurityCheckKind.OrphanedUser, SecurityFindingStatus.Review, "old_app"),
        ]), null);
        var verdict = SecurityAuditVerdict.From(list);

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("2 risks found; the highest: sa login (SQL login · enabled)", verdict.Headline);
        Assert.Equal("Why sa is a risk. Also 1 to review.", verdict.Why);
        Assert.Equal([new VerdictAction(SecurityAuditVerdict.ShowFinding, "Show it"), new VerdictAction(SecurityAuditVerdict.Script, "Script the risks")], verdict.Actions);

        var review = SecurityAuditVerdict.From(new([Finding(SecurityCheckKind.OrphanedUser, SecurityFindingStatus.Review, "old_app")], null));
        Assert.Equal((VerdictLevel.Info, "No risk found; 1 to review, first Orphaned user: old_app"), (review.Level, review.Headline));
        Assert.Equal([new VerdictAction(SecurityAuditVerdict.ShowFinding, "Show it")], review.Actions);
    }

    // ── Alerts ───────────────────────────────────────────────────────────

    private static NamedAlert NamedAlertOf(HealthIndicator indicator, HealthLevel severity, TimeSpan ago, TimeSpan? lasted = null, string connection = "PROD-SQL01") =>
        new(new Alert(Guid.NewGuid(), Guid.NewGuid(), indicator, severity, Now.ToUniversalTime() - ago,
                      lasted is { } l ? Now.ToUniversalTime() - ago + l : null, 95, 90, "", lasted is null ? null : AlertEndReason.Recovered),
            connection);

    [Fact]
    public void Alerts_NoneActive_IsOk_OrUnknownWhenNothingIsMonitored()
    {
        var past = NamedAlertOf(HealthIndicator.Cpu, HealthLevel.Warning, TimeSpan.FromHours(3), TimeSpan.FromMinutes(20));
        var verdict = Invariant(() => AlertsVerdict.From([past], "in the last 7 days", Now.ToUniversalTime(), 1, []));
        Assert.Equal((VerdictLevel.Ok, "No active alerts"), (verdict.Level, verdict.Headline));
        Assert.StartsWith("1 past alert in the last 7 days. The last ended 2 h 40 min ago: ", verdict.Why);
        Assert.EndsWith(" on PROD-SQL01.", verdict.Why);
        Assert.Empty(verdict.Actions);

        var off = AlertsVerdict.From([], "in the last 7 days", Now.ToUniversalTime(), 0, ["PROD-SQL01"]);
        Assert.Equal((VerdictLevel.Unknown, "No alerts are being raised: monitoring is off"), (off.Level, off.Headline));
        Assert.EndsWith(" No past alerts in the last 7 days.", off.Why);
    }

    [Fact]
    public void Alerts_Active_TheCriticalAndLongestFirst()
    {
        var verdict = Invariant(() => AlertsVerdict.From(
            [
                NamedAlertOf(HealthIndicator.Cpu, HealthLevel.Warning, TimeSpan.FromHours(1)),
                NamedAlertOf(HealthIndicator.Blocking, HealthLevel.Critical, TimeSpan.FromMinutes(12), connection: "PROD-SQL02"),
                NamedAlertOf(HealthIndicator.Cpu, HealthLevel.Warning, TimeSpan.FromDays(1), TimeSpan.FromMinutes(5)),
            ],
            "in the last 7 days", Now.ToUniversalTime(), 2, ["DEV"]));

        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.StartsWith("2 active alerts, 1 critical; ", verdict.Headline);
        Assert.Contains(" on PROD-SQL02, critical for 12", verdict.Headline);
        Assert.StartsWith("Worst 1.6 min (threshold ≥ 1.5 min). ", verdict.Why);
        Assert.EndsWith("Monitoring is off for DEV, so no alerts are raised for it.", verdict.Why);
        Assert.Equal([new VerdictAction(AlertsVerdict.Investigate, "Investigate"), new VerdictAction(AlertsVerdict.ActiveOnly, "Active only")], verdict.Actions);

        // Listing the active ones already: nothing more to narrow.
        var narrowed = Invariant(() => AlertsVerdict.From(
            [NamedAlertOf(HealthIndicator.Cpu, HealthLevel.Warning, TimeSpan.FromHours(1)), NamedAlertOf(HealthIndicator.Cpu, HealthLevel.Warning, TimeSpan.FromDays(1), TimeSpan.FromMinutes(5))],
            "in the last 7 days", Now.ToUniversalTime(), 1, [], activeOnly: true));
        Assert.Equal([new VerdictAction(AlertsVerdict.Investigate, "Investigate")], narrowed.Actions);
    }

    // ── Export / AI ──────────────────────────────────────────────────────

    [Fact]
    public void Export_ReadyNothingSelectedNoPath_AndAfterAnExport()
    {
        var ready = ExportVerdict.From(14, 14, @"C:\Reports\SqlVitals_Report.txt", null);
        Assert.Equal((VerdictLevel.Info, "Ready to export 14 of 14 sections"), (ready.Level, ready.Headline));
        Assert.Equal([new VerdictAction(ExportVerdict.Export, "Export now")], ready.Actions);

        Assert.Equal("Nothing to export: no section is selected", ExportVerdict.From(0, 14, @"C:\r.txt", null).Headline);
        Assert.Equal("Choose where to save the report", ExportVerdict.From(3, 14, null, null).Headline);

        var done = Invariant(() => ExportVerdict.From(14, 14, @"C:\Reports\SqlVitals_Report.txt", new(@"C:\Reports\SqlVitals_Report.txt", 14, 120 * 1024, TimeSpan.FromSeconds(3.2))));
        Assert.Equal((VerdictLevel.Ok, "Exported 14 sections to SqlVitals_Report.txt"), (done.Level, done.Headline));
        Assert.StartsWith("120.0 KB in 3.2 s", done.Why);
        Assert.Equal([new VerdictAction(ExportVerdict.Open, "Open the file"), new VerdictAction(ExportVerdict.Export, "Export again")], done.Actions);

        var failed = ExportVerdict.From(14, 14, @"C:\r.txt", new(@"C:\r.txt", 14, 0, TimeSpan.Zero, "Access to the path is denied."));
        Assert.Equal((VerdictLevel.Warning, "The export failed"), (failed.Level, failed.Headline));
        Assert.Equal(@"Access to the path is denied. Nothing was saved to C:\r.txt.", failed.Why);
    }
}
