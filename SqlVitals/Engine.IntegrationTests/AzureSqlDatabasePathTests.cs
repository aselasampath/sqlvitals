using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>
/// The Azure SQL Database paths, where SQL Server can run them (#46). There is no Azure SQL
/// Database in CI, so the repositories are told the server is one (EngineEdition 5) through
/// the capabilities they share, and run the queries they keep for it. These read only views
/// SQL Server has too (sys.database_files, sys.database_scoped_configurations, the
/// performance counters); the Azure-only DMVs (sys.dm_db_wait_stats, sys.dm_db_resource_stats)
/// and database-scoped event sessions are left to the SQL parse tests.
/// </summary>
[Collection(SqlServerTestGroup.Name)]
public class AzureSqlDatabasePathTests(SqlServerFixture server)
{
    private async Task<IWaitStatsRepository> AsAzureSqlDatabaseAsync()
    {
        var real = await new ServerRepository(server.Target).GetCapabilitiesAsync();
        var capabilities = new ServerCapabilitiesCache();
        await capabilities.GetAsync(() => Task.FromResult(real with { EngineEdition = ServerCapabilities.AzureSqlDatabaseEdition }),
                                    CancellationToken.None);
        return new WaitStatsRepository(server.Target, capabilities);
    }

    [SqlServerFact]
    public async Task TheRepositories_SeeAzureSqlDatabase()
    {
        var repo = await AsAzureSqlDatabaseAsync();

        Assert.True(await repo.IsAzureSqlDatabaseAsync());
    }

    [SqlServerFact]
    public async Task Storage_ReadsDatabaseScopedConfigurations_AndTheDatabasesOwnFiles()
    {
        var repo = await AsAzureSqlDatabaseAsync();

        var (config, files, _, topTables) = await repo.GetDatabaseStorageAsync();

        Assert.NotEmpty(config.ToList());
        Assert.All(files.ToList(), f => Assert.Equal(SqlServerFixture.Database, f.DatabaseName));
        Assert.Contains(topTables.ToList(), t => t.TableName == "Orders");
    }

    [SqlServerFact]
    public async Task FileIo_ReadsTheDatabasesOwnFiles_OnAzureStorage()
    {
        var repo = await AsAzureSqlDatabaseAsync();

        var reading = await repo.ReadFileIoAsync();

        Assert.NotEmpty(reading.Files);
        Assert.All(reading.Files, f => Assert.Equal(SqlServerFixture.Database, f.DatabaseName));
        Assert.All(reading.Files, f => Assert.Equal("Azure-managed storage", f.Volume));
    }

    [SqlServerFact]
    public async Task MemorySnapshot_IsReadFromTheCounters()
    {
        var repo = await AsAzureSqlDatabaseAsync();

        var snapshot = await repo.GetMemorySnapshotAsync();

        Assert.True(snapshot.TotalServerMemoryKB > 0);
    }

    [SqlServerFact]
    public async Task ConfigurationChecks_ReadTheDatabasesMaxDop()
    {
        var repo = await AsAzureSqlDatabaseAsync();

        var checks = await repo.GetConfigurationChecksAsync();

        // The note says which server-level checks Azure SQL Database leaves out.
        Assert.Contains("Azure SQL Database", checks.Problem, StringComparison.Ordinal);
        Assert.NotEmpty(checks.Checks);
    }

    [SqlServerFact]
    public async Task SecurityAudit_SaysItIsForSqlServer()
    {
        var repo = await AsAzureSqlDatabaseAsync();

        var audit = await repo.GetSecurityAuditAsync();

        Assert.False(audit.Available);
        Assert.Contains("Azure SQL Database", audit.Problem, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task AgentJobsBackupsAndDeadlocks_SayAzureSqlDatabaseHasNone()
    {
        var repo = await AsAzureSqlDatabaseAsync();

        var jobs = await repo.GetAgentJobsAsync();
        var backups = await repo.GetBackupStatusAsync(new BackupRpo(BackupRpo.DefaultFullMaxAge, BackupRpo.DefaultLogMaxAge));
        var deadlocks = await repo.GetDeadlockHistoryAsync();

        Assert.Contains("Azure SQL Database", jobs.Problem, StringComparison.Ordinal);
        Assert.Contains("Azure SQL Database", backups.Problem, StringComparison.Ordinal);
        Assert.Equal(DeadlockSource.None, deadlocks.Source);
        Assert.Contains("Azure SQL Database", deadlocks.Problem, StringComparison.Ordinal);
    }
}
