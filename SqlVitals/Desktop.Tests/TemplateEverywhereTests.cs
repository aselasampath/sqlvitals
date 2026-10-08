using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Security;
using SqlVitals.Engine.Verdicts;
using SqlVitals.Desktop.Tests.Fakes;
using static SqlVitals.Desktop.Tests.TemplateRolloutTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The page template on the Resources, Maintenance, Configuration and global pages (#150), the last
/// of the rollout: each opens with its verdict, its cards and charts fold, a row's details go to the
/// drawer, and a page that can't read says so. Each page is rendered whole (to
/// %TEMP%\SqlVitalsTests\page-template-rollout). Every page in the sidebar is on the template now.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class TemplateEverywhereTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private MonitoringManager? _monitoring;

    private ConnectionSettingsService Settings() => new(_dir);

    private static IWaitStatsRepository Repo(Dictionary<string, object> results) => EvidencePagesTests.FakeRepository.Create(results);

    private static readonly DateTime Now = DateTime.Now;

    // ── Memory Grants ────────────────────────────────────────────────────

    private static MemorySnapshot Snapshot() =>
        new(32L * 1024 * 1024, 24L * 1024 * 1024, 28L * 1024 * 1024, 18L * 1024 * 1024, 2L * 1024 * 1024, 3L * 1024 * 1024, 0, 2, 6,
            5L * 1024 * 1024, 40L * 1024 * 1024, 30L * 1024 * 1024, "Available physical memory is high", "CONVENTIONAL", Now);

    private static MemoryGrant Grant(int spid, bool pending, int waitMs, long requestedKb, long grantedKb) =>
        new(spid, 0, 1, 4, Now, pending ? null : Now, waitMs, requestedKb, grantedKb, grantedKb / 2, grantedKb / 2, pending ? null : 50, requestedKb, 1024,
            null, null, false, waitMs, "svc_reports", "APP-RPT01", "ReportServer", "SalesDb", "suspended", pending ? "RESOURCE_SEMAPHORE" : null,
            "SELECT * FROM dbo.Orders ORDER BY Total", Now, pending, pending ? "Critical" : "Info");

    private static MemoryClerk Clerk(string name, long mb) => new("MEMORYCLERK_" + name, name, mb * 1024, mb, 5, Now);

    private static IWaitStatsRepository MemoryRepo() => Repo(new()
    {
        [nameof(IWaitStatsRepository.GetMemorySnapshotAsync)] = Task.FromResult(Snapshot()),
        [nameof(IWaitStatsRepository.GetMemoryGrantsAsync)] = Task.FromResult<(IEnumerable<MemoryGrant>, IEnumerable<MemoryClerk>, IEnumerable<MemoryCounter>)>((
            [Grant(51, false, 0, 2_097_152, 2_097_152), Grant(77, true, 41_000, 3_145_728, 0), Grant(78, true, 9_000, 1_048_576, 0)],
            [Clerk("SQLBUFFERPOOL", 18_400), Clerk("SQLQUERYPLAN", 2_100), Clerk("SQLCLR", 300)],
            [])),
    });

    [Fact]
    public void Memory_OpensWithTheQueriesWaitingForMemory() => UiThread.Run(() =>
    {
        var settings = Settings();
        var page = new MemoryGrantsPage(MemoryRepo(), settings);
        Hosted(page, page.RefreshAsync, "memory.png", () =>
        {
            AssertLaidOut(page.Shell, page.Tabs);
            Assert.True(page.ClerksGrid.ActualHeight > 150, $"clerks grid {page.ClerksGrid.ActualHeight}");
        });

        var verdict = page.Shell.Verdict!;
        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.Equal("2 queries wait for a memory grant; session 77 the longest, for 41 s", verdict.Headline);

        // Show the waiting grants: the Grants tab, the longest waiter selected.
        Press(page.Shell.VerdictView, 0);
        Assert.Same(page.TabGrants, page.Tabs.SelectedItem);
        Assert.Equal(77, ((MemoryGrant)page.GrantsGrid.SelectedItem).SessionId);
        Assert.Equal(10, page.GrantsGrid.Columns.Count);
        Assert.Equal(5, page.ClerksGrid.Columns.Count);

        // The figures fold to one line, remembered.
        page.FiguresSection.Toggle.IsChecked = false;
        Assert.StartsWith("SQL Server 24.0 GB of 28.0 GB target · buffer pool 18.0 GB", page.FiguresSection.FoldedSummary);
        Assert.Equal(["figures"], settings.Folded("Memory"));

        // Start and Stop are the top bar's Resume and Pause; the trend's folded line follows them.
        Assert.True(((IPageContext)page).IsPaused);
        ((IPageContext)page).SetPaused(false);
        Assert.False(((IPageContext)page).IsPaused);
        Assert.EndsWith("· every 10 s", page.TrendSection.FoldedSummary);
        ((IPageContext)page).SetPaused(true);
        Assert.True(((IPageContext)page).IsPaused);
        Assert.Equal("SQL Server 24 GB, grants 3 GB · paused: Resume (▶) in the top bar trends it", page.TrendSection.FoldedSummary);
    });

    // ── TempDB ───────────────────────────────────────────────────────────

    [Fact]
    public void TempDb_OpensWithWhoHoldsItAndTheRoomLeft() => UiThread.Run(() =>
    {
        var page = new TempDbPage(Repo(new()
        {
            [nameof(IWaitStatsRepository.GetTempDbPressureAsync)] = Task.FromResult<(IEnumerable<TempDbFile>, IEnumerable<TempDbSession>, IEnumerable<TempDbCounter>)>((
                [new("tempdev", "ROWS", @"T:\tempdb.mdf", 8_192, 2_100, 6_092, 25.6, "64 MB", Now),
                 new("temp2", "ROWS", @"T:\tempdb2.ndf", 8_192, 2_050, 6_142, 25.0, "64 MB", Now),
                 new("templog", "LOG", @"T:\templog.ldf", 2_048, 300, 1_748, 14.6, "64 MB", Now)],
                [new(88, 20_480, 0, 870_400, 0, 890_880, "etl", "APP-ETL01", "Loader", "PAGELATCH_UP", "running", 4_000, "SalesDb", "INSERT #stage SELECT …", Now, "Critical"),
                 new(91, 4_096, 0, 0, 0, 4_096, "app", "APP-WEB03", "OrderService", null, "sleeping", 10, "SalesDb", null, Now, "Info")],
                [new("Version Store Size (KB)", 12_288, Now), new("Free Space in tempdb (KB)", 12_500_000, Now), new("Active Temp Tables", 42, Now)])),
        }), Settings());
        Hosted(page, page.RefreshAsync, "tempdb.png", () => AssertLaidOut(page.Shell, page.SessionsGrid, minDetail: 150));

        var verdict = page.Shell.Verdict!;
        Assert.Equal((VerdictLevel.Warning, "Session 88 holds 870 MB of TempDB"), (verdict.Level, verdict.Headline));
        Press(page.Shell.VerdictView, 0);
        Assert.Equal(88, ((TempDbSession)page.SessionsGrid.SelectedItem).SessionId);
        Assert.Equal("Version Store Size (KB) 12,288 · Free Space in tempdb (KB) 12,500,000 · Active Temp Tables 42", page.CountersSection.FoldedSummary);
        Assert.Equal("3 files · 4,450 MB used of 18,432 MB", page.FilesSection.FoldedSummary);
        Assert.Equal(9, page.SessionsGrid.Columns.Count);
    });

    // ── File I/O ─────────────────────────────────────────────────────────

    private static FileIoCounters Io(int db, string name, int fileId, string type, long reads, long readStall, long writes, long writeStall, string volume) =>
        new(db, name, fileId, $"{name}{(type == "LOG" ? "_log" : "")}", type, $@"{volume}MSSQL\{name}{(type == "LOG" ? ".ldf" : ".mdf")}", volume,
            reads, reads * 65_536, readStall, writes, writes * 8_192, writeStall, 40L << 30);

    private static FileIoReading Reading(int second, params FileIoCounters[] files) => new(Now.AddSeconds(second), Now.AddDays(-3), files);

    [Fact]
    public void FileIo_OpensWithTheFilesSlowerThanYourThreshold() => UiThread.Run(() =>
    {
        var readings = new Queue<FileIoReading>(
        [
            Reading(0, Io(5, "SalesDb", 1, "ROWS", 1_000, 5_000, 100, 200, @"D:\"), Io(5, "SalesDb", 2, "LOG", 0, 0, 1_000, 2_000, @"L:\"),
                       Io(6, "HrDb", 1, "ROWS", 500, 1_000, 10, 10, @"E:\")),
            Reading(10, Io(5, "SalesDb", 1, "ROWS", 1_400, 26_200, 120, 260, @"D:\"), Io(5, "SalesDb", 2, "LOG", 0, 0, 1_500, 4_000, @"L:\"),
                        Io(6, "HrDb", 1, "ROWS", 600, 1_300, 10, 10, @"E:\")),
        ]);
        var repo = SequenceRepository.Create(new() { [nameof(IWaitStatsRepository.ReadFileIoAsync)] = () => Task.FromResult(readings.Count > 1 ? readings.Dequeue() : readings.Peek()) });
        var settings = Settings();
        var page = new FileIoPage(repo, settings);
        Hosted(page, async () =>
        {
            await page.RefreshAsync();
            await Task.Delay(100);   // shown once the page is laid out
            await page.RefreshAsync();
        }, "file-io.png", () => AssertLaidOut(page.Shell, page.FilesGrid, minDetail: 150));

        var verdict = page.Shell.Verdict!;
        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("1 file is slower than your threshold; SalesDb · SalesDb reads in 53.0 ms", verdict.Headline);
        Assert.Contains(@"A volume is slow as a whole (D:\)", verdict.Why);
        Assert.Equal("Show the slow files", Assert.Single(verdict.Actions).Text);

        // Show the slow files: only those, the slowest in the drawer.
        Press(page.Shell.VerdictView, 0);
        Assert.Equal(1, page.CmbFilter.SelectedIndex);
        Assert.Equal("SalesDb", Assert.IsType<FileLatency>(Assert.Single(page.FilesGrid.Items)).DatabaseName);
        Assert.Equal("SalesDb · SalesDb", page.Details.Title);
        Assert.Contains("Volume: D:\\", page.DetailsView!.RowTexts);
        Assert.False(page.DetailsView.TextShown);
        Assert.Equal(16, page.FilesGrid.Columns.Count);
        Assert.Equal(10, page.VolumesGrid.Columns.Count);

        // How it's measured folds to one line, remembered; Pause is the top bar's.
        page.MeasureSection.Toggle.IsChecked = false;
        Assert.Equal("The last interval · every 10 s · data files over 20 ms, log files over 10 ms", page.MeasureSection.FoldedSummary);
        Assert.Equal(["measure"], settings.Folded("FileIo"));
        ((IPageContext)page).SetPaused(true);
        Assert.Equal("--", page.TxtCountdown.Text);
        Assert.EndsWith("(paused) · data files over 20 ms, log files over 10 ms", page.MeasureSection.FoldedSummary);
    });

    // ── DB Storage ───────────────────────────────────────────────────────

    private static DatabaseFile DbFile(string db, string type, string logical, decimal sizeMb, decimal maxMb = -1, bool autoShrink = false) =>
        new(db, "ONLINE", "FULL", 160, "Latin1_General_CI_AS", autoShrink, true, true, true, false, false, type, logical,
            $@"D:\MSSQL\{logical}.{(type == "LOG" ? "ldf" : "mdf")}", sizeMb, sizeMb * 0.8m, sizeMb * 0.2m, maxMb, type == "LOG" ? "512 MB" : "10%");

    private static IWaitStatsRepository StorageRepo() => Repo(new()
    {
        [nameof(IWaitStatsRepository.GetDatabaseStorageAsync)] = Task.FromResult<(IEnumerable<ServerConfigParam>, IEnumerable<DatabaseFile>, IEnumerable<TempDbFileSummary>, IEnumerable<TableStorage>)>((
            [new("max degree of parallelism", 8, 0, 32767, "maximum degree of parallelism", true, true),
             new("cost threshold for parallelism", 50, 0, 32767, "cost threshold for parallelism", true, true)],
            [DbFile("SalesDb", "ROWS", "SalesDb", 412_000), DbFile("SalesDb", "LOG", "SalesDb_log", 30_720, maxMb: 32_768),
             DbFile("HrDb", "ROWS", "HrDb", 2_048, autoShrink: true), DbFile("HrDb", "LOG", "HrDb_log", 512)],
            [new("tempdev", "ROWS", @"T:\tempdb.mdf", 8_192, 2_100, 6_092)],
            [new("dbo", "Orders", 48_000_000, 92_000, 31_000, 123_000, 122_400, 600, 6, "Clustered")])),
    });

    [Fact]
    public void DbStorage_OpensWithTheFileNearItsLimit() => UiThread.Run(() =>
    {
        var page = new DatabaseStoragePage(StorageRepo(), Settings());
        Hosted(page, page.RefreshAsync, "db-storage.png", () => AssertLaidOut(page.Shell, page.Tabs));

        var verdict = page.Shell.Verdict!;
        Assert.Equal((VerdictLevel.Warning, "1 file is near its maximum size; SalesDb · SalesDb_log at 94 %"), (verdict.Level, verdict.Headline));

        page.Tabs.SelectedItem = page.TabTables;
        Press(page.Shell.VerdictView, 0);
        Assert.Same(page.TabFiles, page.Tabs.SelectedItem);
        Assert.Equal("SalesDb_log", ((DatabaseFileVm)page.GridFiles.SelectedItem).LogicalFileName);
        Assert.Equal("434.84 GB in all · data 404.34 GB · log 30.50 GB · TempDB 8.00 GB · MAXDOP 8 · cost threshold 50", page.SizesSection.FoldedSummary);
        Assert.Equal([15, 6, 6, 10], new[] { page.GridFiles, page.GridTempDb, page.GridConfig, page.GridTables }.Select(g => g.Columns.Count));
    });

    [Fact]
    public void DbStorage_OpenedAgain_GetsItsTabBack() => UiThread.Run(() =>
    {
        ViewState state = null!;
        var first = new DatabaseStoragePage(StorageRepo());
        Hosted(first, first.RefreshAsync, null, () =>
        {
            first.Tabs.SelectedItem = first.TabConfig;
            state = ViewState.Capture(first);
        });

        var again = new DatabaseStoragePage(StorageRepo());
        Hosted(again, async () =>
        {
            await again.RefreshAsync();
            state.Restore(again);
            // The state is put back at ContextIdle: wait for the dispatcher to get there, not a
            // fixed time, which a busy test run can outlast.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }, null);

        Assert.Same(again.TabConfig, again.Tabs.SelectedItem);
    });

    // ── Perfmon ──────────────────────────────────────────────────────────

    [Fact]
    public void Perfmon_OpensWithTheCountersPastTheirLimit() => UiThread.Run(() =>
    {
        static PerfmonCounter Counter(string name, long value, int type = 65792) => new("SQLServer:Memory Manager", name, "", value, type, Now);
        var page = new PerfmonPage(Repo(new()
        {
            [nameof(IWaitStatsRepository.GetPerfmonCountersAsync)] = Task.FromResult<IEnumerable<PerfmonCounter>>(
            [
                Counter("Batch Requests/sec", 1_200_000, WaitStatsRepository.PerfmonRateCounterType),
                Counter("Memory Grants Pending", 3),
                Counter("Target Server Memory (KB)", 28_000_000),
                Counter("Total Server Memory (KB)", 24_000_000),
            ]),
        }), null, Settings());

        // Before anything is read: what to do.
        Assert.Equal("Not charting yet", page.Shell.Verdict!.Headline);

        Hosted(page, page.RefreshAsync, null);
        Assert.Equal((VerdictLevel.Ok, "1 counter charted, none past a known limit"), (page.Shell.Verdict!.Level, page.Shell.Verdict.Headline));

        page.CmbGroup.SelectedItem = "Memory Pressure";
        Hosted(page, () => Task.CompletedTask, "perfmon.png", () => AssertLaidOut(page.Shell, page.PerfmonChart));
        var verdict = page.Shell.Verdict!;
        Assert.Equal((VerdictLevel.Warning, "Memory Grants Pending is 3: queries wait for a memory grant"), (verdict.Level, verdict.Headline));
        Assert.Equal("Memory Grants", Assert.Single(verdict.Actions).Text);

        ((IPageContext)page).SetPaused(false);
        Assert.False(((IPageContext)page).IsPaused);
        ((IPageContext)page).SetPaused(true);
        Assert.True(((IPageContext)page).IsPaused);
    });

    // ── Index Health ─────────────────────────────────────────────────────

    private static MissingIndex Missing(string table, double impact, string severity, long seeks) =>
        new("SalesDb", "dbo", table, "[CustomerId], [Status]", "[OrderDate]", "[Total]", 12, seeks, 40, Now, null, 3.2, 87.5, impact, severity,
            $"CREATE NONCLUSTERED INDEX [IX_{table}_CustomerId_Status] ON [dbo].[{table}] ([CustomerId], [Status], [OrderDate]) INCLUDE ([Total]);", Now);

    [Fact]
    public void IndexHealth_OpensWithTheMostValuableMissingIndex() => UiThread.Run(() =>
    {
        var page = new IndexHealthPage(Repo(new()
        {
            [nameof(IWaitStatsRepository.GetIndexHealthAsync)] = Task.FromResult<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)>((
                [Missing("OrderLines", 54_000, "WARNING", 3_100), Missing("Orders", 310_000, "CRITICAL", 18_400), Missing("Notes", 900, "INFO", 12)],
                [new("SalesDb", "dbo", "Orders", "IX_Orders_Legacy", "NONCLUSTERED", false, false, false, 0, 0, 0, 912_000, null, null, Now, 0, 48_000_000, Now)])),
            [nameof(IWaitStatsRepository.GetIndexUsagePatternsAsync)] = Task.FromResult<IEnumerable<IndexUsagePattern>>(
                [new("Orders", "PK_Orders", "CLUSTERED", 4_000_000, 120, 0, 900_000, Now, Now, null, Now, "Healthy", "SalesDb", Now)]),
            [nameof(IWaitStatsRepository.SupportsOnlineIndexRebuildAsync)] = Task.FromResult(true),
            [nameof(IWaitStatsRepository.GetIndexFragmentationAsync)] = Task.FromResult<IEnumerable<IndexFragmentation>>(
                [new("dbo", "Orders", "IX_Orders_Date", "NONCLUSTERED INDEX", 41.5, 120_000, 71.2, 48_000_000, "SalesDb", "Rebuild", Now)]),
        }), Settings());
        Hosted(page, page.RefreshAsync, "index-health.png", () => AssertLaidOut(page.Shell, page.Tabs));

        var verdict = page.Shell.Verdict!;
        Assert.Equal((VerdictLevel.Warning, "3 missing indexes suggested, 1 critical; the most valuable on SalesDb.dbo.Orders"), (verdict.Level, verdict.Headline));

        // Show it: the Missing Indexes tab, the suggestion and its CREATE statement in the drawer.
        page.Tabs.SelectedItem = page.TabFrag;
        page.MissingFilter.Text = "OrderLines";
        Press(page.Shell.VerdictView, 0);
        Assert.Same(page.TabMissing, page.Tabs.SelectedItem);
        Assert.Equal("", page.MissingFilter.Text);
        Assert.Equal("Orders", ((MissingIndex)page.MissingGrid.SelectedItem).TableName);
        Assert.Equal("dbo.Orders", page.Details.Title);
        Assert.StartsWith("CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId_Status]", page.DetailsView!.Text);
        Assert.Contains("Severity: CRITICAL", page.DetailsView.RowTexts);
        Press(page.Shell.VerdictView, 1);
        Assert.Same(page.TabUnused, page.Tabs.SelectedItem);

        Assert.Equal([10, 9, 11, 10], new[] { page.MissingGrid, page.UnusedGrid, page.UsageGrid, page.FragGrid }.Select(g => g.Columns.Count));
        Assert.True(page.ChkOnlineRebuild.IsEnabled);
    });

    // ── Stale Stats ──────────────────────────────────────────────────────

    [Fact]
    public void StaleStats_OpensWithTheMostChangedStatistic() => UiThread.Run(() =>
    {
        static StaleStatistic Stat(string table, string name, long rows, long changes, int days) =>
            new("dbo", table, name, Now.AddDays(-days), days, rows, rows / 4, 25, changes, false, name.StartsWith("_WA", StringComparison.Ordinal), false, "SalesDb", Now);
        var settings = Settings();
        var page = new StaleStatisticsPage(Repo(new()
        {
            [nameof(IWaitStatsRepository.GetStaleStatisticsAsync)] = Task.FromResult<IEnumerable<StaleStatistic>>(
                [Stat("Orders", "IX_Orders_Date", 48_000_000, 2_100_000, 9), Stat("OrderLines", "_WA_Sys_00000003_2A4B4B5E", 900_000, 410_000, 31),
                 Stat("Customers", "PK_Customers", 1_200_000, 12_000, 4)]),
        }), settings);
        Hosted(page, page.RefreshAsync, "stale-stats.png", () => AssertLaidOut(page.Shell, page.StatsGrid));

        var verdict = page.Shell.Verdict!;
        Assert.Equal((VerdictLevel.Warning, "1 statistic has 20 % or more of its table changed; the most, SalesDb.dbo.OrderLines (_WA_Sys_00000003_2A4B4B5E)"),
                     (verdict.Level, verdict.Headline));
        Press(page.Shell.VerdictView, 0);
        Assert.Equal("OrderLines", ((StaleStatistic)page.StatsGrid.SelectedItem).TableName);
        Assert.Equal("Generate UPDATE STATISTICS Script (1 selected)", page.BtnUpdateStatsScript.Content);

        page.AboutSection.Toggle.IsChecked = false;
        Assert.Equal(["about"], settings.Folded("StaleStats"));
        Assert.Equal(11, page.StatsGrid.Columns.Count);
    });

    // ── Agent Jobs ───────────────────────────────────────────────────────

    private static AgentJob Job(string name, JobOutcome? last, TimeSpan? runningFor = null, string? message = null) =>
        new(Guid.NewGuid(), name, true, "Database Maintenance", "sa", $"{name}, nightly.", last, Now.AddHours(-6), TimeSpan.FromMinutes(12), message,
            30, last == JobOutcome.Failed ? 2 : 0, 28, TimeSpan.FromMinutes(12), runningFor is null ? null : Now - runningFor, runningFor,
            Now.AddHours(18), Now.AddHours(18).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));

    [Fact]
    public void AgentJobs_OpensWithTheFailedAndLongRunningJobs() => UiThread.Run(() =>
    {
        var jobs = AgentJobList.Sort(
        [
            Job("Nightly backup", JobOutcome.Failed, message: "The job failed. The last step to run was step 2 (Backup)."),
            Job("Reindex SalesDb", JobOutcome.Succeeded, runningFor: TimeSpan.FromMinutes(55)),
            Job("Purge history", JobOutcome.Succeeded),
        ]);
        static JobStepRun Step(int id, string name, JobOutcome outcome, string message) => new(id, name, outcome, Now.AddHours(-6), TimeSpan.FromMinutes(3), message);
        // The first job's runs are read at once; another's never come back, as on a slow msdb.
        var runReads = 0;
        IReadOnlyList<JobRun> runs = [new(Now.AddHours(-6), JobOutcome.Failed, TimeSpan.FromMinutes(6), "The job failed.",
                                          [Step(1, "Check space", JobOutcome.Succeeded, "Done."), Step(2, "Backup", JobOutcome.Failed, "BACKUP DATABASE is terminating abnormally: disk full.")])];
        var page = new AgentJobsPage(SequenceRepository.Create(new()
        {
            [nameof(IWaitStatsRepository.GetAgentJobsAsync)] = () => Task.FromResult(new AgentJobList(jobs, AgentServiceState.Running, Now, null)),
            [nameof(IWaitStatsRepository.GetAgentJobRunsAsync)] = () => ++runReads <= 3 ? Task.FromResult(runs) : new TaskCompletionSource<IReadOnlyList<JobRun>>().Task,
        }), Settings());
        Hosted(page, async () => { await page.RefreshAsync(); await Task.Delay(50); }, "agent-jobs.png", () => AssertLaidOut(page.Shell, page.JobsGrid, minDetail: 100));

        var verdict = page.Shell.Verdict!;
        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.Equal("1 job failed its last run: Nightly backup; 1 is running longer than usual: Reindex SalesDb", verdict.Headline);
        Assert.StartsWith("Nightly backup: Last run failed", verdict.Why);

        // Show its runs: the failed job selected (past a filter that hid it), its details and the
        // failing step's message in the drawer.
        page.JobsFilter.Text = "Purge";
        Press(page.Shell.VerdictView, 0);
        UiThread.Wait(Task.Delay(50));
        Assert.Equal("Nightly backup", ((AgentJob)page.JobsGrid.SelectedItem).Name);
        Assert.Equal("Nightly backup", page.Details.Title);
        Assert.Contains("Owner: sa", page.DetailsView!.RowTexts);
        Assert.Contains("BACKUP DATABASE is terminating abnormally: disk full.", page.DetailsView.Text);

        // Another job: none of the first one's runs or steps stay under it while its own are read.
        runReads = 3;
        page.JobsGrid.SelectedItem = page.JobsGrid.Items.OfType<AgentJob>().Single(j => j.Name == "Purge history");
        Assert.Equal("Purge history", page.Details.Title);
        Assert.Null(page.RunsGrid.ItemsSource);
        Assert.False(page.DetailsView.TextShown);

        Press(page.Shell.VerdictView, 1);
        Assert.Equal(1, page.CmbFilter.SelectedIndex);
        Assert.Equal(2, page.JobsGrid.Items.Count);
        Assert.Equal(13, page.JobsGrid.Columns.Count);
        Assert.Equal(6, page.StepsGrid.Columns.Count);
    });

    // ── Config Checks and Security Audit ─────────────────────────────────

    [Fact]
    public void ConfigChecks_OpensWithTheRiskiestFailingCheck() => UiThread.Run(() =>
    {
        var page = new ConfigChecksPage(Repo(new()
        {
            [nameof(IWaitStatsRepository.GetConfigurationChecksAsync)] = Task.FromResult(new ConfigCheckList(ConfigCheckList.Sort(
            [
                new(ConfigCheckKind.MaxDop, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, "0 (all 16 cores)", "8",
                    "With MAXDOP 0 one query can take every core.", "EXEC sp_configure 'max degree of parallelism', 8;"),
                new(ConfigCheckKind.MaxServerMemory, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, "2,147,483,647 MB (unlimited)", "28,672 MB",
                    "Unlimited, SQL Server can take memory Windows needs.", "EXEC sp_configure 'max server memory (MB)', 28672;"),
                new(ConfigCheckKind.CostThreshold, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, "50", "50 or more", "Fine."),
                new(ConfigCheckKind.AutoShrink, "All 12 databases", ConfigCheckStatus.Pass, "OFF", "OFF", "Fine."),
            ]), null)),
        }), "PROD-SQL01", Settings());
        Hosted(page, page.RefreshAsync, "config-checks.png", () => AssertLaidOut(page.Shell, page.ChecksGrid));

        var verdict = page.Shell.Verdict!;
        Assert.Equal((VerdictLevel.Warning, "2 of 4 checks fail; the riskiest: Max server memory (Server)"), (verdict.Level, verdict.Headline));
        Assert.Equal(["Show it", "Script the warnings"], verdict.Actions.Select(a => a.Text));

        page.CmbFilter.SelectedIndex = 2;   // server settings: still lists it
        page.ChecksGrid.SelectedItem = null;
        Press(page.Shell.VerdictView, 0);
        Assert.Equal(ConfigCheckKind.MaxServerMemory, Assert.IsType<ConfigCheck>(page.ChecksGrid.SelectedItem).Kind);
        Assert.Equal("Max server memory · Server", page.Details.Title);
        Assert.Equal("EXEC sp_configure 'max server memory (MB)', 28672;", page.DetailsView!.Text);
        Assert.Contains("Recommended: 28,672 MB", page.DetailsView.RowTexts);
        Assert.Equal(6, page.ChecksGrid.Columns.Count);

        // A filter that leaves no check: the drawer says what to select again.
        page.CmbFilter.SelectedIndex = 4;   // not checked: none
        Assert.Null(page.Details.Body);
        Assert.Equal("Check details", page.Details.Title);
    });

    [Fact]
    public void SecurityAudit_OpensWithTheHighestRisk() => UiThread.Run(() =>
    {
        var page = new SecurityAuditPage(Repo(new()
        {
            [nameof(IWaitStatsRepository.GetSecurityAuditAsync)] = Task.FromResult(new SecurityFindingList(SecurityFindingList.Sort(
            [
                new(SecurityCheckKind.SysadminMember, SecurityFinding.ServerScope, @"CORP\svc_reports", SecurityFindingStatus.Risk, "Windows login · sysadmin",
                    "A service account in sysadmin can do anything if it is compromised.", @"ALTER SERVER ROLE sysadmin DROP MEMBER [CORP\svc_reports];"),
                new(SecurityCheckKind.SaLogin, SecurityFinding.ServerScope, "sa", SecurityFindingStatus.Risk, "SQL login · enabled",
                    "sa is the first login an attacker tries.", "ALTER LOGIN [sa] DISABLE;"),
                new(SecurityCheckKind.Trustworthy, "SalesDb", "SalesDb", SecurityFindingStatus.Pass, "OFF", "Fine."),
                new(SecurityCheckKind.OrphanedUser, "HrDb", "old_app", SecurityFindingStatus.Review, "SQL user · no login", "Nobody can log in as it."),
            ]), null)),
        }), "PROD-SQL01", Settings());
        Hosted(page, page.RefreshAsync, "security-audit.png", () => AssertLaidOut(page.Shell, page.FindingsGrid));

        var verdict = page.Shell.Verdict!;
        Assert.Equal((VerdictLevel.Warning, "2 risks found; the highest: sa login (SQL login · enabled)"), (verdict.Level, verdict.Headline));
        Assert.Equal("sa is the first login an attacker tries. Also 1 to review.", verdict.Why);

        page.CmbFilter.SelectedIndex = 6;   // orphaned users: hides it
        Press(page.Shell.VerdictView, 0);
        Assert.Equal(1, page.CmbFilter.SelectedIndex);
        Assert.Equal(SecurityCheckKind.SaLogin, ((SecurityFinding)page.FindingsGrid.SelectedItem).Kind);
        Assert.Equal("ALTER LOGIN [sa] DISABLE;", page.DetailsView!.Text);
        Assert.Equal(6, page.FindingsGrid.Columns.Count);
    });

    // ── Alerts ───────────────────────────────────────────────────────────

    [Fact]
    public void Alerts_OpensWithTheWorstActiveAlert() => UiThread.Run(() =>
    {
        // Hot (CPU at 99 %) monitored for three samples, enough to alert; Quiet left out on purpose
        // (not monitored in the background), so not named as off.
        var calls = new ConcurrentQueue<(string, string)>();
        var settings = Settings();
        static ConnectionSettings Conn(string server, string name) => new() { Server = server, Name = name, Database = "Sales", Authentication = SqlAuthMode.Windows };
        var (hot, quiet) = (Conn("hot", "Hot"), Conn("quiet", "Quiet"));
        settings.Upsert(hot);
        settings.Upsert(quiet);
        settings.SetActive(hot.Id);
        settings.SetStartMonitoringAutomatically(true);
        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                            (conn, _) => MonitoringManagerTests.HotRepository.Create(conn.Server, calls));
        _monitoring.Sync(settings.Load(), ActivitySources.AutomaticAtStartup);
        for (var i = 0; i < 3; i++)
            UiThread.Wait(_monitoring.Get(hot.Id)!.CollectNowAsync());

        var navigator = new FakeNavigator();
        var page = new AlertsPage(_monitoring, settings, navigator: navigator);
        Hosted(page, page.RefreshAsync, "alerts.png", () => AssertLaidOut(page.Shell, page.AlertsGrid));

        var verdict = page.Shell.Verdict!;
        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.StartsWith("1 active alert; ", verdict.Headline);
        Assert.Contains(" on Hot, critical for ", verdict.Headline);
        Assert.EndsWith("No past alerts in the last 7 days.", verdict.Why);

        // Investigate, through the navigator (#182): the alert's evidence, logged as from the Alerts page.
        Press(page.Shell.VerdictView, 0);
        var (investigated, source) = Assert.Single(navigator.Investigated);
        Assert.Equal(hot.Id, investigated.ConnectionId);
        Assert.Equal(ActivitySources.AlertsPage, source);

        page.AlertsGrid.SelectedItem = page.AlertsGrid.Items[0];
        Assert.Contains("Connection: Hot", page.DetailsView!.RowTexts);
        Assert.Contains("Status: Active", page.DetailsView.RowTexts);
        Assert.Equal(12, page.AlertsGrid.Columns.Count);
    });

    // ── Export / AI ──────────────────────────────────────────────────────

    [Fact]
    public void Export_SaysWhatItWillWrite_AndWhatIsMissing() => UiThread.Run(() =>
    {
        var page = new ExportPage(Repo(new()), Settings());
        page.TxtFilePath.Text = @"C:\Reports";
        Hosted(page, () => Task.CompletedTask, "export.png", () => AssertLaidOut(page.Shell, page.GroupList, minDetail: 150));

        var count = ExportService.AllGroups.Length;
        Assert.Equal((VerdictLevel.Info, $"Ready to export {count} of {count} sections"), (page.Shell.Verdict!.Level, page.Shell.Verdict.Headline));
        Assert.Equal(@"To C:\Reports\SqlVitals_Report.txt: plain UTF-8 text with labelled sections and tables, read from the open server now.", page.Shell.Verdict.Why);

        page.BtnSelectNone.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal("Nothing to export: no section is selected", page.Shell.Verdict!.Headline);
        page.BtnSelectAll.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        page.TxtFileName.Text = "";
        Assert.Equal("Choose where to save the report", page.Shell.Verdict!.Headline);
    });

    // ── A page that can't read says so in its verdict ────────────────────

    public static TheoryData<string> Unreadable => new() { "Memory", "TempDb", "FileIo", "DbStorage", "Perfmon", "IndexHealth", "StaleStats",
                                                           "AgentJobs", "ConfigChecks", "SecurityAudit" };

    [Theory, MemberData(nameof(Unreadable))]
    public void APageThatCantRead_SaysSoInItsVerdict(string tag) => UiThread.Run(() =>
    {
        var error = new InvalidOperationException("Timeout expired.");
        var repo  = SequenceRepository.Create(new()
        {
            [nameof(IWaitStatsRepository.GetMemorySnapshotAsync)]      = () => Task.FromException<MemorySnapshot>(error),
            [nameof(IWaitStatsRepository.GetMemoryGrantsAsync)]        = () => Task.FromException<(IEnumerable<MemoryGrant>, IEnumerable<MemoryClerk>, IEnumerable<MemoryCounter>)>(error),
            [nameof(IWaitStatsRepository.GetTempDbPressureAsync)]      = () => Task.FromException<(IEnumerable<TempDbFile>, IEnumerable<TempDbSession>, IEnumerable<TempDbCounter>)>(error),
            [nameof(IWaitStatsRepository.ReadFileIoAsync)]             = () => Task.FromException<FileIoReading>(error),
            [nameof(IWaitStatsRepository.GetDatabaseStorageAsync)]     = () => Task.FromException<(IEnumerable<ServerConfigParam>, IEnumerable<DatabaseFile>, IEnumerable<TempDbFileSummary>, IEnumerable<TableStorage>)>(error),
            [nameof(IWaitStatsRepository.GetPerfmonCountersAsync)]     = () => Task.FromException<IEnumerable<PerfmonCounter>>(error),
            [nameof(IWaitStatsRepository.GetIndexHealthAsync)]         = () => Task.FromException<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)>(error),
            [nameof(IWaitStatsRepository.GetStaleStatisticsAsync)]     = () => Task.FromException<IEnumerable<StaleStatistic>>(error),
            [nameof(IWaitStatsRepository.GetAgentJobsAsync)]           = () => Task.FromException<AgentJobList>(error),
            [nameof(IWaitStatsRepository.GetConfigurationChecksAsync)] = () => Task.FromException<ConfigCheckList>(error),
            [nameof(IWaitStatsRepository.GetSecurityAuditAsync)]       = () => Task.FromException<SecurityFindingList>(error),
        });
        ICodeBehindPage page = tag switch
        {
            "Memory"        => new MemoryGrantsPage(repo),
            "TempDb"        => new TempDbPage(repo),
            "FileIo"        => new FileIoPage(repo, Settings()),
            "DbStorage"     => new DatabaseStoragePage(repo),
            "Perfmon"       => new PerfmonPage(repo),
            "IndexHealth"   => new IndexHealthPage(repo),
            "StaleStats"    => new StaleStatisticsPage(repo),
            "AgentJobs"     => new AgentJobsPage(repo),
            "ConfigChecks"  => new ConfigChecksPage(repo, "PROD-SQL01"),
            _               => new SecurityAuditPage(repo, "PROD-SQL01"),
        };

        Assert.Throws<InvalidOperationException>(() => UiThread.Wait(page.RefreshAsync()));

        var verdict = ((PageShell)((Page)page).FindName("Shell")).Verdict!;
        Assert.Equal(VerdictLevel.Unknown, verdict.Level);
        Assert.StartsWith("Couldn't read ", verdict.Headline);
        Assert.Equal("Error: Timeout expired. Refresh to try again.", verdict.Why);
    });

    // ── Every page in the sidebar is on the template ─────────────────────

    /// <summary>
    /// The pages that keep a layout of their own, and why. Any other page in the sidebar is on
    /// <see cref="PageShell"/>: a new page fails here until it is, or until it is added with a reason.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> OwnLayout = new Dictionary<string, string>
    {
        ["Home"]        = "every server as a tile (#130): a dashboard of servers, not one server's evidence",
        ["Overview"]    = "a server's summary cards (#143): the verdicts of the other pages, gathered",
        ["LiveMetrics"] = "full-size live charts (#143) beside Overview, with nothing to judge on their own",
        ["Settings"]    = "a category menu and editors (#131), not a page of the server's data",
    };

    private static bool OnPageShell(Type page) =>
        page.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Any(f => f.FieldType == typeof(PageShell));

    [Fact]
    public void EveryPageInTheSidebar_IsOnPageShell()
    {
        var notOnIt = NavMenu.AllPages.Select(p => p.Tag)
            .Where(tag => !OwnLayout.ContainsKey(tag) && !OnPageShell(PageCatalog.Types[tag]))
            .ToList();
        Assert.True(notOnIt.Count == 0, $"Not on PageShell: {string.Join(", ", notOnIt)}");

        // An exception that went onto the template, or out of the sidebar, is dropped from the list.
        Assert.All(OwnLayout.Keys, tag =>
        {
            Assert.Contains(tag, NavMenu.AllPages.Select(p => p.Tag));
            Assert.False(OnPageShell(PageCatalog.Types[tag]), $"{tag} is on PageShell now: remove it from OwnLayout.");
        });
    }

    [Fact]
    public void EveryPageOfThisStory_IsOnTheTemplate_WithItsKeyAndKind()
    {
        UiThread.Run(() =>
        {
            var repo = Repo(new());
            var settings = Settings();
            (string Key, Page Page, PageKind Kind)[] pages =
            [
                ("Memory", new MemoryGrantsPage(repo, settings), PageKind.Live),
                ("TempDb", new TempDbPage(repo, settings), PageKind.Snapshot),
                ("FileIo", new FileIoPage(repo, settings), PageKind.Live),
                ("DbStorage", new DatabaseStoragePage(repo, settings), PageKind.Snapshot),
                ("Perfmon", new PerfmonPage(repo, null, settings), PageKind.Live),
                ("IndexHealth", new IndexHealthPage(repo, settings), PageKind.Snapshot),
                ("StaleStats", new StaleStatisticsPage(repo, settings), PageKind.Snapshot),
                ("AgentJobs", new AgentJobsPage(repo, settings), PageKind.Snapshot),
                ("ConfigChecks", new ConfigChecksPage(repo, "PROD-SQL01", settings), PageKind.Snapshot),
                ("SecurityAudit", new SecurityAuditPage(repo, "PROD-SQL01", settings), PageKind.Snapshot),
                ("Export", new ExportPage(repo, settings), PageKind.None),
            ];
            foreach (var (key, page, kind) in pages)
            {
                Assert.Equal(page.GetType(), PageCatalog.Types[key]);
                Assert.Equal(kind, ((IPageContext)page).Kind);
                var shell = Assert.IsType<PageShell>(page.Content);
                Assert.Equal(key, shell.PageKey);
                Assert.NotNull(shell.Folds);
            }
        });
    }

    public void Dispose()
    {
        if (_monitoring is { } monitoring)
            UiThread.Run(monitoring.Dispose);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
