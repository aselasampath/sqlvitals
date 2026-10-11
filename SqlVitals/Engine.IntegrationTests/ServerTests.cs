using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>What the server is, its waits, memory and live counters.</summary>
[Collection(SqlServerTestGroup.Name)]
public class ServerTests(SqlServerFixture server)
{
    private readonly IWaitStatsRepository _repo = server.NewRepository();

    [SqlServerFact]
    [Covers(nameof(IServerRepository.GetCapabilitiesAsync), nameof(IServerRepository.IsAzureSqlDatabaseAsync),
            nameof(IServerRepository.SupportsOnlineIndexRebuildAsync))]
    public async Task Capabilities_AreSqlServers_AndTheSysadminsPermissions()
    {
        var capabilities = await _repo.GetCapabilitiesAsync();

        Assert.Equal(AzureKind.None, capabilities.Azure);
        Assert.Equal(server.MajorVersion, capabilities.MajorVersion);
        Assert.True(capabilities.PermissionsKnown);
        Assert.Contains("VIEW SERVER STATE", capabilities.Permissions);
        Assert.False(await _repo.IsAzureSqlDatabaseAsync());
        Assert.Equal(capabilities.SupportsOnlineIndexRebuild, await _repo.SupportsOnlineIndexRebuildAsync());
    }

    [SqlServerFact]
    [Covers(nameof(IWaitsRepository.GetCumulativeWaitsAsync), nameof(IWaitsRepository.GetTopWaitTypesAsync),
            nameof(IWaitsRepository.GetSignalVsResourceAsync))]
    public async Task Waits_SinceTheServerStarted()
    {
        Assert.NotEmpty((await _repo.GetCumulativeWaitsAsync()).ToList());
        Assert.NotEmpty((await _repo.GetTopWaitTypesAsync()).ToList());
        Assert.NotNull(await _repo.GetSignalVsResourceAsync());
    }

    [SqlServerFact]
    [Covers(nameof(IWaitsRepository.GetActiveWaitsAsync))]
    public async Task ActiveWaits_HaveTheSessionWaitingOnALock()
    {
        var waits = (await _repo.GetActiveWaitsAsync()).ToList();

        var waiter = Assert.Single(waits, w => w.SessionId == server.WaiterSessionId);
        Assert.Equal(server.BlockerSessionId, waiter.BlockingSessionId);
        Assert.StartsWith("LCK_M_", waiter.WaitType, StringComparison.Ordinal);
    }

    [SqlServerFact]
    [Covers(nameof(IMemoryRepository.GetMemoryGrantsAsync))]
    public async Task MemoryGrants_ClerksAndCounters()
    {
        var (grants, clerks, counters) = await _repo.GetMemoryGrantsAsync();

        _ = grants.ToList();
        Assert.NotEmpty(clerks.ToList());
        Assert.NotEmpty(counters.ToList());
    }

    [SqlServerFact]
    [Covers(nameof(IMemoryRepository.GetMemorySnapshotAsync))]
    public async Task MemorySnapshot_HasTheServersMemory()
    {
        var snapshot = await _repo.GetMemorySnapshotAsync();

        Assert.True(snapshot.PhysicalMemoryKB > 0);
        Assert.True(snapshot.TotalServerMemoryKB > 0);
    }

    [SqlServerFact]
    [Covers(nameof(ILiveMetricsRepository.GetLiveMetricsAsync))]
    public async Task LiveMetrics_CountTheBlockedSession()
    {
        var metrics = await _repo.GetLiveMetricsAsync();

        Assert.True(metrics.BlockedSessions >= 1);
        Assert.True(metrics.PageLifeExpectancy >= 0);
    }

    [SqlServerFact]
    [Covers(nameof(ILiveMetricsRepository.GetLiveMetricsAsync))]
    public async Task LiveMetrics_ReadTheBufferPool_AndTheFullestLogsSizeAndReuseWait()
    {
        // What the health score judges page life expectancy and log space by (#173).
        var metrics = await _repo.GetLiveMetricsAsync();

        Assert.True(metrics.BufferPoolPages > 0, $"BufferPoolPages {metrics.BufferPoolPages}");
        Assert.False(string.IsNullOrEmpty(metrics.LogUsedDatabase));
        Assert.True(metrics.LogSizeMb > 0, $"LogSizeMb {metrics.LogSizeMb} for {metrics.LogUsedDatabase}");
        Assert.False(string.IsNullOrEmpty(metrics.LogReuseWait),
                     $"No reuse wait for {metrics.LogUsedDatabase}. " +
                     await server.DescribeAsync("SELECT name, log_reuse_wait_desc FROM sys.databases"));
    }

    [SqlServerFact]
    [Covers(nameof(ILiveMetricsRepository.GetPerfmonCountersAsync))]
    public async Task PerfmonCounters_AreRead()
    {
        Assert.NotEmpty((await _repo.GetPerfmonCountersAsync()).ToList());
    }

    [SqlServerFact]
    [Covers(nameof(ITempDbRepository.GetTempDbPressureAsync))]
    public async Task TempDb_HasItsFiles_AndTheSessionWithATempTable()
    {
        var (files, sessions, counters) = await _repo.GetTempDbPressureAsync();

        Assert.NotEmpty(files.ToList());
        Assert.Contains(sessions.ToList(), s => s.SessionId == server.BlockerSessionId);
        Assert.NotEmpty(counters.ToList());
    }
}
