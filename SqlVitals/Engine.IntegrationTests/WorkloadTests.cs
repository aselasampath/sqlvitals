using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>Indexes, statistics, the plan cache, storage and sessions, after the fixture's workload.</summary>
[Collection(SqlServerTestGroup.Name)]
public class WorkloadTests(SqlServerFixture server)
{
    private readonly IWaitStatsRepository _repo = server.NewRepository();

    [SqlServerFact]
    [Covers(nameof(IIndexRepository.GetIndexHealthAsync))]
    public async Task IndexHealth_FindsTheMissingIndex_AndTheUnusedOne()
    {
        var (missing, unused) = await _repo.GetIndexHealthAsync();

        Assert.True(missing.Any(m => m.DatabaseName == SqlServerFixture.Database && m.TableName == "Orders"),
                    await server.DescribeAsync("SELECT database_id, statement, equality_columns, inequality_columns FROM sys.dm_db_missing_index_details"));
        Assert.True(unused.Any(u => u.DatabaseName == SqlServerFixture.Database && u.IndexName == "IX_Orders_Unused"),
                    await server.DescribeAsync("""
                        SELECT i.name, u.user_seeks, u.user_scans, u.user_lookups, u.user_updates
                        FROM sys.indexes i
                        LEFT JOIN sys.dm_db_index_usage_stats u
                            ON u.object_id = i.object_id AND u.index_id = i.index_id AND u.database_id = DB_ID()
                        WHERE i.object_id = OBJECT_ID(N'dbo.Orders')
                        """));
    }

    [SqlServerFact]
    [Covers(nameof(IIndexRepository.GetIndexUsagePatternsAsync))]
    public async Task IndexUsage_HasTheOrdersIndexes()
    {
        var usage = (await _repo.GetIndexUsagePatternsAsync()).ToList();

        Assert.Contains(usage, u => u.IndexName == "IX_Orders_CustomerCode");
    }

    [SqlServerFact]
    [Covers(nameof(IIndexRepository.GetIndexFragmentationAsync))]
    public async Task Fragmentation_FindsTheIndexKeyedOnNewId()
    {
        var fragmented = (await _repo.GetIndexFragmentationAsync(minFragmentationPercent: 0, minPageCount: 0)).ToList();

        Assert.Contains(fragmented, f => f.IndexName == "PK_Events" && f.FragmentationPercent > 0);
    }

    [SqlServerFact]
    [Covers(nameof(IIndexRepository.GetStaleStatisticsAsync))]
    public async Task StaleStatistics_HaveTheChangedOnes()
    {
        var stale = (await _repo.GetStaleStatisticsAsync()).ToList();

        Assert.Contains(stale, s => s.TableName == "Orders" && s.ModificationsSinceLastUpdate > 0);
    }

    [SqlServerFact]
    [Covers(nameof(IResourceQueryRepository.GetResourceIntensiveQueriesAsync))]
    public async Task ResourceIntensiveQueries_ByReadsCpuAndHighestReads()
    {
        var (byReads, byCpu, byHighestReads) = await _repo.GetResourceIntensiveQueriesAsync(topN: 20);

        Assert.NotEmpty(byReads.ToList());
        Assert.NotEmpty(byCpu.ToList());
        Assert.NotEmpty(byHighestReads.ToList());
    }

    [SqlServerFact]
    [Covers(nameof(IResourceQueryRepository.GetObjectQueryPlanAsync))]
    public async Task ObjectQueryPlan_IsTheProceduresCachedPlan()
    {
        var plan = await _repo.GetObjectQueryPlanAsync("dbo.GetCustomerOrders");

        Assert.NotNull(plan);
        Assert.Contains("ShowPlanXML", plan, StringComparison.Ordinal);
    }

    [SqlServerFact]
    [Covers(nameof(IResourceQueryRepository.GetImplicitConversionsAsync))]
    public async Task ImplicitConversions_FindTheNVarcharComparison()
    {
        var conversions = (await _repo.GetImplicitConversionsAsync()).ToList();

        Assert.NotEmpty(conversions);
    }

    [SqlServerFact]
    [Covers(nameof(IPlanCacheHealthRepository.GetPlanCacheHealthAsync))]
    public async Task PlanCacheHealth_HasThePlanGuide()
    {
        var (summary, multiPlan, keyLookups, misestimates, skewed, guides) = await _repo.GetPlanCacheHealthAsync();

        Assert.NotNull(summary);
        _ = multiPlan.ToList();
        _ = keyLookups.ToList();
        _ = misestimates.ToList();
        _ = skewed.ToList();
        Assert.Contains(guides.ToList(), g => g.GuideName == "PG_Orders_MaxDop1");
    }

    [SqlServerFact]
    [Covers(nameof(IStorageRepository.GetDatabaseStorageAsync))]
    public async Task Storage_HasTheDatabasesFiles_AndItsLargestTable()
    {
        var (config, files, tempDbFiles, topTables) = await _repo.GetDatabaseStorageAsync();

        Assert.NotEmpty(config.ToList());
        Assert.Contains(files.ToList(), f => f.DatabaseName == SqlServerFixture.Database);
        Assert.NotEmpty(tempDbFiles.ToList());
        Assert.Contains(topTables.ToList(), t => t.TableName == "Orders");
    }

    [SqlServerFact]
    [Covers(nameof(ISessionRepository.GetProcessesAsync))]
    public async Task Processes_HaveTheBlockingChain()
    {
        var processes = (await _repo.GetProcessesAsync()).ToList();

        Assert.Contains(processes, p => p.SessionId == server.BlockerSessionId);
        Assert.Contains(processes, p => p.SessionId == server.WaiterSessionId && p.ParentId == server.BlockerSessionId);
    }

    [SqlServerFact]
    [Covers(nameof(ISessionRepository.GetSessionPlanAsync))]
    public async Task SessionPlan_OfTheRunningRequest_AndOfTheSleepingBlocker()
    {
        var waiting = await _repo.GetSessionPlanAsync(server.WaiterSessionId);
        var sleeping = await _repo.GetSessionPlanAsync(server.BlockerSessionId);

        Assert.True(waiting.IsRunning);
        Assert.NotNull(waiting.PlanXml);
        Assert.False(sleeping.IsRunning);
    }

    [SqlServerFact]
    [Covers(nameof(ISessionRepository.GetApplicationConnectionsAsync))]
    public async Task ApplicationConnections_HaveTheTests()
    {
        var applications = (await _repo.GetApplicationConnectionsAsync()).ToList();

        Assert.Contains(applications, a => a.ApplicationName == "SqlVitals integration tests" && a.CurrentConnections >= 2);
    }
}
