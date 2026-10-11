using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Monitoring;

/// <summary>
/// The slow checks (#174): disk free graded per volume on both thresholds, backups past their RPO
/// and failed Agent jobs, read on their own schedule and trusted only while the read is fresh.
/// </summary>
public sealed class ServerChecksTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    private static T InCulture<T>(string name, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ServerCheckFigures Figures(IReadOnlyList<VolumeSpace>? volumes = null, IReadOnlyList<string>? pastRpo = null,
                                              IReadOnlyList<string>? failedJobs = null, long? lastJob = 10) =>
        new(volumes ?? [new VolumeSpace(@"D:\", 1000, 500)], null, pastRpo ?? [], null, failedJobs ?? [], null, lastJob);

    private static HealthReading Disk(params VolumeSpace[] volumes) =>
        HealthRules.Read(HealthIndicator.DiskFree, 0, measured: true, new HealthFacts(Volumes: volumes), HealthThresholds.Default);

    // ── Disk free ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 30, HealthLevel.Healthy)]    // 30 %
    [InlineData(100, 15, HealthLevel.Warning)]    // 15 % and 15 GB: below 20 % and 50 GB
    [InlineData(100, 5, HealthLevel.Critical)]    // 5 % and 5 GB: below 10 % and 20 GB
    [InlineData(2000, 240, HealthLevel.Healthy)]  // 12 %, but 240 GB is plenty
    [InlineData(2000, 40, HealthLevel.Warning)]   // 2 %, under 50 GB but not 20 GB
    [InlineData(2000, 10, HealthLevel.Critical)]  // 0.5 % and 10 GB
    public void AVolume_IsPastALevel_OnlyWhenBothItsShareAndItsGbAreBelowIt(double totalGb, double freeGb, HealthLevel expected)
    {
        Assert.Equal(expected, HealthRules.DiskLevelOf(new VolumeSpace(@"E:\", totalGb, freeGb), HealthThresholds.Default));
    }

    [Fact]
    public void ABlankGb_LeavesTheLevelToThePct_AndABlankPctTurnsItOff()
    {
        var volume  = new VolumeSpace(@"E:\", 2000, 240);   // 12 %, 240 GB
        var pctOnly = HealthThresholds.Default with { DiskFreeGb = new HealthThreshold(null, 20) };
        var off     = HealthThresholds.Default.With(HealthIndicator.DiskFree, new HealthThreshold(null, 10));
        var tiny    = new VolumeSpace(@"F:\", 40, 36);        // 90 % free, 36 GB: under the 50 GB, but the % is off

        Assert.Equal(HealthLevel.Warning, HealthRules.DiskLevelOf(volume, pctOnly));
        Assert.Equal(HealthLevel.Healthy, HealthRules.DiskLevelOf(volume, off));
        Assert.Equal(HealthLevel.Healthy, HealthRules.DiskLevelOf(tiny, off));
    }

    [Fact]
    public void TheCheck_ReportsTheVolumePastTheWorstLevel_WithTheLeastFreeSpace()
    {
        var reading = InCulture("en-GB", () => Disk(new VolumeSpace(@"C:\", 100, 30), new VolumeSpace(@"E:\", 100, 15),
                                                   new VolumeSpace(@"F:\", 100, 18), new VolumeSpace(@"L:\", 4000, 100)));

        Assert.Equal((HealthLevel.Warning, 15.0, @"E:\"), (reading.Level, reading.Value, reading.Facts!.Volume));
        Assert.Equal(new HealthThreshold(20, 10), reading.Limits);
        Assert.Equal(@"Disk E:\ 15% free (15 GB)", reading.Reason);
        Assert.Equal(@"disk E:\ 15 % free (15 GB)", InCulture("en-GB", () => HealthRules.Phrase(reading)));
    }

    [Fact]
    public void AVolumeSparedByItsGb_IsHealthy_AndTheNoteSaysWhy()
    {
        var reading = InCulture("en-GB", () => Disk(new VolumeSpace(@"E:\", 2000, 240)));

        Assert.Equal(HealthLevel.Healthy, reading.Level);
        Assert.Equal(@"Not a warning: E:\ is only 12 % free, but still has 240 GB.", reading.Note);
    }

    [Fact]
    public void Regrading_UsesEveryVolume_NotOnlyTheOneReported()
    {
        var reading = Disk(new VolumeSpace(@"C:\", 100, 30), new VolumeSpace(@"E:\", 100, 15));
        var tighter = HealthThresholds.Default.With(HealthIndicator.DiskFree, new HealthThreshold(35, 16));

        var regraded = HealthRules.Regrade(reading, tighter);

        Assert.Equal((HealthLevel.Critical, @"E:\"), (regraded.Level, regraded.Facts!.Volume));
    }

    // ── Backups and jobs ──────────────────────────────────────────────────────

    [Fact]
    public void ADatabasePastItsRpo_IsCritical_AndNamed()
    {
        var readings = InCulture("en-GB", () => TestChecks.Readings(Figures(pastRpo: ["Sales"])));
        var backups  = readings.Single(r => r.Indicator == HealthIndicator.BackupsPastRpo);

        Assert.Equal((HealthLevel.Critical, 1.0), (backups.Level, backups.Value));
        Assert.Equal("1 past backup RPO (Sales)", backups.Reason);
        Assert.Equal("Sales past its backup RPO", HealthRules.Phrase(backups));
    }

    [Fact]
    public void SeveralDatabasesPastTheirRpo_NameTheFirstThree()
    {
        var backups = TestChecks.Readings(Figures(pastRpo: ["A", "B", "C", "D", "E"]))
                                .Single(r => r.Indicator == HealthIndicator.BackupsPastRpo);

        Assert.Equal("5 past backup RPO (A, B, C +2)", backups.Reason);
        Assert.Equal("5 databases past their backup RPO", InCulture("en-GB", () => HealthRules.Phrase(backups)));
    }

    [Fact]
    public void AFailedJob_IsAWarning()
    {
        var jobs = TestChecks.Readings(Figures(failedJobs: ["Nightly ETL"])).Single(r => r.Indicator == HealthIndicator.FailedJobs);

        Assert.Equal((HealthLevel.Warning, "1 Agent job(s) failed (Nightly ETL)"), (jobs.Level, jobs.Reason));
        Assert.Equal("Agent job Nightly ETL failed", HealthRules.Phrase(jobs));
    }

    [Fact]
    public void NoneFailed_AndNonePast_ArePassingFigures()
    {
        var score = HealthScore.From(TestChecks.Healthy(), T0);

        Assert.All(score.Checks, c => Assert.Equal(HealthLevel.Healthy, c.Level));
        Assert.Equal(["50 %", "None past", "None failed"], InCulture("en-GB", () => score.Checks.Select(c => c.ValueText).ToList()));
    }

    [Fact]
    public void APartTheServerCouldntAnswer_IsUnknown_WithWhy()
    {
        var figures  = new ServerCheckFigures([new VolumeSpace(@"D:\", 100, 50)], null, null, "No msdb backup history.", null, "No Agent here.");
        var readings = TestChecks.Readings(figures);

        Assert.Equal([HealthLevel.Healthy, HealthLevel.Unknown, HealthLevel.Unknown], readings.Select(r => r.Level));
        Assert.Equal(["No msdb backup history.", "No Agent here."], readings.Skip(1).Select(r => r.Note));
        Assert.All(readings.Skip(1), r => Assert.False(r.Measured));
    }

    [Fact]
    public void NoVolumeReported_IsUnknown_NotHealthy()
    {
        var disk = TestChecks.Readings(Figures(volumes: [])).Single(r => r.Indicator == HealthIndicator.DiskFree);

        Assert.Equal((HealthLevel.Unknown, "No volume holding database files was reported."), (disk.Level, disk.Note));
    }

    [Fact]
    public void BackupsPastRpo_CountsWhatTheBackupsPageFlags_OnlyOnTheReplicaThatShouldTakeThem()
    {
        var now = new DateTime(2026, 10, 11, 9, 0, 0);
        DatabaseBackupRow Row(string name, int fullDaysAgo, bool inAg = false, bool preferred = true) =>
            new(name, "SIMPLE", "ONLINE", false, now.AddYears(-1), inAg, true, false,
                new BackupInfo(now.AddDays(-fullDaysAgo), now.AddDays(-fullDaysAgo), 1, false, null), null, null, preferred);

        var list = BackupList.From([Row("Fresh", 1), Row("Stale", 9), Row("AgHere", 9, inAg: true), Row("AgElsewhere", 9, inAg: true, preferred: false)],
                                   now, BackupRpo.Default);

        Assert.Equal(["AgHere", "Stale"], Engine.Repositories.ServerChecksRepository.PastRpo(list).Order());
    }

    [Fact]
    public void AGreyBar_SaysWhyItHasNoData()
    {
        var score = HealthScore.From(new ServerChecks().Readings(HealthThresholds.Default, T0), T0);
        var input = new Engine.Home.TileInput(Guid.NewGuid(), "UAT", "uat", "", false, true, score);

        var disk = Engine.Home.ServerTile.From(input, T0).Bars.Single(b => b.Indicator == HealthIndicator.DiskFree);

        Assert.Equal("Disk free: no data, so it's left out of the score. Not read yet: read every 5 min.", disk.ToolTip);
    }

    [Theory]
    [InlineData(42, "SQL01", "SQL01", 50, 42L)]     // the same server: failures since the row seen
    [InlineData(42, "SQL01", "sql01", 50, 42L)]
    [InlineData(42, "SQL01", "SQL02", 50, null)]    // failed over to another replica: start again
    [InlineData(42, "SQL01", "SQL01", 10, null)]    // its history started again
    [InlineData(42, null, "SQL01", 50, 42L)]        // a read from before the server was known
    public void FailuresAreCountedAfterTheRowSeen_OnlyOnTheServerItWasSeenOn(long after, string? seenOn, string now, long last, long? expected)
    {
        var request = new ServerChecksRequest(BackupRpo.Default, after, TimeSpan.FromMinutes(5), seenOn);

        Assert.Equal(expected, Engine.Repositories.ServerChecksRepository.AfterInstanceId(request, now, last));
    }

    [Fact]
    public void TheFailedJobsQuery_NeverConvertsAJobsTimeThatCouldFail()
    {
        Assert.Contains("TRY_CONVERT", Engine.Repositories.ServerChecksRepository.FailedJobsSql);
        Assert.DoesNotContain("DATETIMEFROMPARTS", Engine.Repositories.ServerChecksRepository.FailedJobsSql);
        Assert.Contains("@@SERVERNAME", Engine.Repositories.ServerChecksRepository.JobHistoryEndSql);
    }

    [Fact]
    public void Volumes_ByMountPoint_OrByPathWhereLinuxGivesNone()
    {
        static dynamic Row(string? mount, string path, long total, long free)
        {
            dynamic row = new System.Dynamic.ExpandoObject();
            row.Volume = mount; row.PhysicalName = path; row.TotalBytes = total; row.FreeBytes = free;
            return row;
        }
        const long Gb = 1L << 30;

        var volumes = Engine.Repositories.ServerChecksRepository.ToVolumes(
        [
            Row(@"D:\", @"D:\Data\Sales.mdf", 100 * Gb, 40 * Gb),
            Row(@"D:\", @"D:\Data\Hr.mdf", 100 * Gb, 39 * Gb),          // read a moment later: the least counts
            Row(null, "/var/opt/mssql/data/master.mdf", 72 * Gb, 11 * Gb),
            Row(null, "/var/opt/mssql/data/model.mdf", 72 * Gb, 11 * Gb),
            Row(null, "/var/opt/mssql/data/x.mdf", 0, 0),                  // no size: left out
        ]);

        Assert.Equal([("/var/opt/mssql/data/", 72.0, 11.0), (@"D:\", 100.0, 39.0)],
                     volumes.Select(v => (v.Volume, v.TotalGb, v.FreeGb)));
    }

    // ── Schedule and age ──────────────────────────────────────────────────────

    [Fact]
    public void DueAtFirst_ThenAnIntervalAfterEachReadStarted_WorkedOrNot()
    {
        var checks = new ServerChecks { Interval = TimeSpan.FromMinutes(5) };

        Assert.True(checks.IsDue(T0));
        var first = checks.Start(T0, BackupRpo.Default);
        Assert.Equal((null, TimeSpan.FromMinutes(5)), (first.JobsAfterInstanceId, first.FirstLookBack));

        Assert.False(checks.IsDue(T0.AddMinutes(4.9)));
        Assert.True(checks.IsDue(T0.AddMinutes(5)));
    }

    [Fact]
    public void TheNextRead_AsksForFailuresAfterTheLastJobRowSeen_KeptThroughAReadThatCouldntSeeThem()
    {
        var checks = new ServerChecks();
        checks.Start(T0, BackupRpo.Default);
        checks.Apply(Figures(lastJob: 42), T0);

        checks.Start(T0.AddMinutes(5), BackupRpo.Default);
        checks.Apply(Figures(lastJob: null) with { FailedJobs = null, JobsNoData = "No permission." }, T0.AddMinutes(5));

        Assert.Equal(42, checks.Start(T0.AddMinutes(10), BackupRpo.Default).JobsAfterInstanceId);
    }

    [Fact]
    public void BeforeTheFirstRead_EveryCheckIsUnknown()
    {
        var readings = new ServerChecks().Readings(HealthThresholds.Default, T0);

        Assert.Equal(ServerChecks.Indicators, readings.Select(r => r.Indicator));
        Assert.All(readings, r => Assert.Equal((HealthLevel.Unknown, "Not read yet: read every 5 min."), (r.Level, r.Note)));
    }

    [Fact]
    public void SamplesFurtherApartThanTheInterval_SetTheReadsTime()
    {
        // Read after a sample, so every 5 min at best with 5-minute samples and a 1-minute interval.
        var checks = new ServerChecks { Interval = TimeSpan.FromMinutes(1), SampleInterval = TimeSpan.FromMinutes(5) };
        checks.Apply(Figures(failedJobs: ["Nightly"]), T0);

        Assert.Equal((TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(11)), (checks.Every, checks.StaleAfter));
        Assert.Equal(HealthLevel.Warning, checks.Readings(HealthThresholds.Default, T0.AddMinutes(5))[2].Level);
        Assert.Equal(TimeSpan.FromMinutes(5), checks.Start(T0.AddMinutes(5), BackupRpo.Default).FirstLookBack);
    }

    [Fact]
    public void AFailedJob_IsHeld_LongEnoughForAnAlertToStart()
    {
        // 10 samples a minute apart to start an alert: longer than the 5-minute reads.
        var checks = new ServerChecks { SampleInterval = TimeSpan.FromMinutes(1), AlertSamples = 10 };
        checks.Apply(Figures(failedJobs: ["Nightly ETL"]), T0);
        checks.Apply(Figures(), T0.AddMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(11), checks.FailureHold);
        Assert.Equal(1.0, checks.Readings(HealthThresholds.Default, T0.AddMinutes(10))[2].Value);

        checks.Apply(Figures(), T0.AddMinutes(12));
        Assert.Equal(0.0, checks.Readings(HealthThresholds.Default, T0.AddMinutes(12))[2].Value);
    }

    [Fact]
    public void WithTheDefaults_AFailedJob_CountsUntilTheNextRead()
    {
        var checks = new ServerChecks();
        checks.Apply(Figures(failedJobs: ["Nightly ETL"]), T0);

        Assert.Equal(1.0, checks.Readings(HealthThresholds.Default, T0.AddMinutes(4))[2].Value);
        checks.Apply(Figures(), T0.AddMinutes(5));
        Assert.Equal(0.0, checks.Readings(HealthThresholds.Default, T0.AddMinutes(5))[2].Value);
    }

    [Fact]
    public void TheJobHistoryRowSeen_IsAskedFor_OnTheServerItWasSeenOn()
    {
        var checks = new ServerChecks();
        checks.Apply(Figures(lastJob: 42) with { JobsServer = "SQL01" }, T0);

        var next = checks.Start(T0.AddMinutes(5), BackupRpo.Default);

        Assert.Equal((42L, "SQL01"), (next.JobsAfterInstanceId, next.JobsServer));
    }

    [Fact]
    public void AReadOlderThanTwoIntervalsAndAMinute_IsTooOldToTrust()
    {
        var checks = new ServerChecks { Interval = TimeSpan.FromMinutes(5) };
        checks.Apply(Figures(pastRpo: ["Sales"]), T0);

        Assert.Equal(TimeSpan.FromMinutes(11), checks.StaleAfter);
        Assert.Equal(HealthLevel.Critical, checks.Readings(HealthThresholds.Default, T0.AddMinutes(11))[1].Level);

        var stale = InCulture("en-GB", () => checks.Readings(HealthThresholds.Default, T0.AddMinutes(12)));
        Assert.All(stale, r => Assert.Equal(HealthLevel.Unknown, r.Level));
        Assert.Equal("Last read 12 min ago, too old to trust: it is read every 5 min.", stale[0].Note);
    }

    [Fact]
    public void Reset_ForgetsTheRead_AndIsDueAgain()
    {
        var checks = new ServerChecks();
        checks.Start(T0, BackupRpo.Default);
        checks.Apply(Figures(), T0);

        checks.Reset();

        Assert.True(checks.IsDue(T0));
        Assert.Null(checks.ReadUtc);
        Assert.All(checks.Readings(HealthThresholds.Default, T0), r => Assert.Equal(HealthLevel.Unknown, r.Level));
    }

    // ── Thresholds ────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_DiskWarnsBelow20PctAnd50Gb_BackupsCriticalAtOne_JobsWarnAtOne()
    {
        var d = HealthThresholds.Default;

        Assert.Equal(new HealthThreshold(20, 10), d.Get(HealthIndicator.DiskFree));
        Assert.Equal(new HealthThreshold(50, 20), d.DiskFreeGbOrDefault);
        Assert.Equal(new HealthThreshold(null, 1), d.Get(HealthIndicator.BackupsPastRpo));
        Assert.Equal(new HealthThreshold(1, null), d.Get(HealthIndicator.FailedJobs));
    }

    [Fact]
    public void AFileSavedBefore174_GetsTheNewChecksDefaults()
    {
        var old = new HealthThresholds(new(75, 90), new(30, 120), new(300, null), new(1, null), new(80, 90), new(80, 90));

        var read = HealthThresholds.Normalize(old);

        Assert.Equal(HealthThresholds.Default.DiskFreeGbOrDefault, read.DiskFreeGb);
        Assert.Equal(HealthThresholds.Default.Get(HealthIndicator.DiskFree), read.Get(HealthIndicator.DiskFree));
    }

    [Fact]
    public void ABadGbThreshold_IsPutBack_AndTyping_ReadsGb()
    {
        var read = HealthThresholds.Normalize(HealthThresholds.Default with { DiskFreeGb = new HealthThreshold(10, 50) });
        Assert.Equal(HealthThresholds.DiskFreeGbInfo.Default, read.DiskFreeGb);

        Assert.True(HealthThresholds.TryParse(HealthThresholds.DiskFreeGbInfo, "100 GB", "", out var typed, out _));
        Assert.Equal(new HealthThreshold(100, null), typed);
        Assert.False(HealthThresholds.TryParse(HealthThresholds.DiskFreeGbInfo, "10", "50", out _, out var error));
        Assert.Contains("lower than the warning", error);
    }

    [Fact]
    public void AlertText_SaysWhatIsHappening_OnTheNewChecks()
    {
        var connection = Guid.NewGuid();
        Alert Alert(HealthIndicator indicator, HealthLevel severity, double value, double? threshold, string detail) =>
            new(Guid.NewGuid(), connection, indicator, severity, T0, null, value, threshold, detail);

        var texts = InCulture("en-GB", () => new[]
        {
            AlertText.Happening(Alert(HealthIndicator.DiskFree, HealthLevel.Critical, 6, 10, @"Disk E:\ 6% free (6 GB)")),
            AlertText.Happening(Alert(HealthIndicator.BackupsPastRpo, HealthLevel.Critical, 2, 1, "2 past backup RPO (Sales, HR)")),
            AlertText.Happening(Alert(HealthIndicator.FailedJobs, HealthLevel.Warning, 1, 1, "1 Agent job(s) failed (Nightly ETL)")),
        });

        Assert.Equal(@"A volume holding database files is down to 6 % free (critical at < 10 %). Disk E:\ 6% free (6 GB).", texts[0]);
        Assert.Equal("2 databases are past their backup RPO (critical at ≥ 1): a restore would lose more than it allows. " +
                     "2 past backup RPO (Sales, HR).", texts[1]);
        Assert.Equal("1 SQL Server Agent job failed (warning at ≥ 1). 1 Agent job(s) failed (Nightly ETL).", texts[2]);
    }
}
