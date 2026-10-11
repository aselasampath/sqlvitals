using System.Diagnostics;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Security;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>Agent jobs, backups, configuration, the security audit, deadlocks and file I/O.</summary>
[Collection(SqlServerTestGroup.Name)]
public class ServerHealthTests(SqlServerFixture server)
{
    private static readonly BackupRpo Rpo = new(BackupRpo.DefaultFullMaxAge, BackupRpo.DefaultLogMaxAge);

    private readonly IWaitStatsRepository _repo = server.NewRepository();

    [SqlServerFact]
    [Covers(nameof(IAgentJobRepository.GetAgentJobsAsync), nameof(IAgentJobRepository.GetAgentJobRunsAsync))]
    public async Task AgentJobs_AndTheRunsOfOne()
    {
        var list = await _repo.GetAgentJobsAsync();

        Assert.Null(list.Problem);
        if (!server.AgentRan)
        {
            Assert.Empty(await _repo.GetAgentJobRunsAsync(Guid.NewGuid(), runningSince: null));
            return;
        }

        var succeeded = Assert.Single(list.Jobs, j => j.Name == "SqlVitalsIT succeeds");
        var failed = Assert.Single(list.Jobs, j => j.Name == "SqlVitalsIT fails");
        Assert.True(failed.FailedRuns >= 1);

        var runs = await _repo.GetAgentJobRunsAsync(succeeded.JobId, runningSince: null);
        Assert.NotEmpty(runs);
    }

    [SqlServerFact]
    [Covers(nameof(IBackupRepository.GetBackupStatusAsync), nameof(IBackupRepository.GetBackupHistoryAsync))]
    public async Task Backups_FullDifferentialAndLog_AndTheirHistory()
    {
        var list = await _repo.GetBackupStatusAsync(Rpo);

        Assert.Null(list.Problem);
        var database = Assert.Single(list.Databases, d => d.Name == SqlServerFixture.Database);
        Assert.NotNull(database.Full);
        Assert.NotNull(database.Differential);
        Assert.NotNull(database.Log);

        var history = await _repo.GetBackupHistoryAsync(SqlServerFixture.Database, database.Row.CreateDate);
        Assert.Equal(3, history.Count);
    }

    [SqlServerFact]
    [Covers(nameof(IConfigurationRepository.GetConfigurationChecksAsync))]
    public async Task ConfigurationChecks_AreAllRead()
    {
        var checks = await _repo.GetConfigurationChecksAsync();

        Assert.Null(checks.Problem);
        Assert.NotEmpty(checks.Checks);
    }

    [SqlServerFact]
    [Covers(nameof(ISecurityAuditRepository.GetSecurityAuditAsync))]
    public async Task SecurityAudit_FindsTheOrphanedUser()
    {
        var audit = await _repo.GetSecurityAuditAsync();

        Assert.True(audit.Available);
        Assert.Contains(audit.Findings, f => f.Kind == SecurityCheckKind.OrphanedUser
                                             && f.Status != SecurityFindingStatus.Pass
                                             && (f.Subject + f.Finding).Contains("SqlVitalsIT_Orphan", StringComparison.Ordinal));
    }

    [SqlServerFact]
    [Covers(nameof(IFileIoRepository.ReadFileIoAsync))]
    public async Task FileIo_HasTheDatabasesFiles()
    {
        var reading = await _repo.ReadFileIoAsync();

        var unnamed = reading.Files.Where(f => f.FileType.Length == 0).Select(f => $"{f.DatabaseName} ({f.DatabaseId}) {f.FileName}");
        Assert.True(reading.Note is null, $"{reading.Note} {string.Join(", ", unnamed)}");
        Assert.Contains(reading.Files, f => f.DatabaseName == SqlServerFixture.Database);
        // Each volume's free space, the disk check's evidence (#174).
        Assert.NotEmpty(reading.Space);
    }

    [SqlServerFact]
    [Covers(nameof(IServerChecksRepository.GetServerChecksAsync))]
    public async Task ServerChecks_ReadVolumesBackupsAndFailedJobs()
    {
        // The first read counts the failures of the last ten years: the fixture's failed run.
        var first = await _repo.GetServerChecksAsync(new ServerChecksRequest(Rpo, null, TimeSpan.FromDays(3650)));

        Assert.Equal((null, null, null), (first.VolumesNoData, first.BackupsNoData, first.JobsNoData));
        Assert.NotEmpty(first.Volumes!);
        Assert.All(first.Volumes!, v => Assert.True(v.TotalGb > 0 && v.FreeGb >= 0 && v.FreeGb <= v.TotalGb, $"{v}"));
        // Backed up just now: full, differential and log.
        Assert.DoesNotContain(SqlServerFixture.Database, first.PastRpo!);
        Assert.NotNull(first.LastJobInstanceId);
        if (!server.AgentRan)
        {
            Assert.Empty(first.FailedJobs!);
            return;
        }
        Assert.Contains("SqlVitalsIT fails", first.FailedJobs!);
        Assert.DoesNotContain("SqlVitalsIT succeeds", first.FailedJobs!);

        // The next read counts only what failed since: nothing ran in between.
        var next = await _repo.GetServerChecksAsync(new ServerChecksRequest(Rpo, first.LastJobInstanceId, TimeSpan.FromDays(3650)));
        Assert.Empty(next.FailedJobs!);
        Assert.Equal(first.LastJobInstanceId, next.LastJobInstanceId);
    }

    /// <summary>
    /// system_health writes the fixture's deadlock out after its dispatch latency, so this waits
    /// for it rather than read once.
    /// </summary>
    [SqlServerFact]
    [Covers(nameof(IDeadlockRepository.GetDeadlockHistoryAsync))]
    public async Task DeadlockHistory_HasTheFixturesDeadlock()
    {
        var clock = Stopwatch.StartNew();
        DeadlockHistory history;
        while (true)
        {
            history = await _repo.GetDeadlockHistoryAsync();
            Assert.Null(history.Problem);
            if (history.Deadlocks.Count > 0 || clock.Elapsed > TimeSpan.FromMinutes(3))
                break;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Assert.NotEqual(DeadlockSource.None, history.Source);
        // A server the tests ran on before keeps the earlier runs' deadlocks too.
        var ours = history.Deadlocks.Where(d => d.Xml.Contains("LockA", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(ours);
        Assert.All(ours, d => Assert.Single(d.Victims));
    }
}
