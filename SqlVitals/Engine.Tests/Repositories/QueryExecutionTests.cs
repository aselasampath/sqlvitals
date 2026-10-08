using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

/// <summary>
/// How every query reaches the server (#167), seen through a recording connection: the caller's
/// token goes with it, a cancelled one is never an error, the lock timeout is set on every read,
/// and the server's capabilities are read once per connection and choose the query.
/// </summary>
public sealed class QueryExecutionTests
{
    private static readonly SqlTarget Page       = new("Server=sql01;User ID=vitals;Password=x");
    private static readonly SqlTarget Monitoring = Page with { LockTimeoutMilliseconds = SqlTarget.MonitoringLockTimeoutMilliseconds };

    private static Func<string, System.Data.DataTable?> Server(int edition, string version, params string[] permissions) => sql =>
        sql.Contains("SERVERPROPERTY('ProductVersion')", StringComparison.Ordinal)
            ? RecordingConnection.Row(("Edition", edition), ("ProductVersion", version))
            : sql.Contains("fn_my_permissions", StringComparison.Ordinal)
                ? RecordingConnection.Column("PermissionName", permissions)
                : null;

    // ── The caller's token ───────────────────────────────────────────────

    [Fact]
    public async Task TheCallersToken_ReachesTheCommand()
    {
        var conn = new RecordingConnection();
        var repo = new WaitsRepository(Page) { Connect = () => conn };
        using var cancel = new CancellationTokenSource();

        await repo.GetTopWaitTypesAsync(cancel.Token);

        Assert.Equal(cancel.Token, Assert.Single(conn.Commands).Token);
    }

    [Fact]
    public async Task AQueryCancelledWhileItRuns_ThrowsCancelled_NotAnError()
    {
        var conn = new RecordingConnection { Hangs = "SET LOCK_TIMEOUT" };
        var repo = new WaitsRepository(Page) { Connect = () => conn };
        using var cancel = new CancellationTokenSource();

        var reading = repo.GetTopWaitTypesAsync(cancel.Token);
        await cancel.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ATokenAlreadyCancelled_SendsNothingAndIsNotAnError()
    {
        var conn = new RecordingConnection();
        var repo = new WaitsRepository(Page) { Connect = () => conn };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.GetTopWaitTypesAsync(new CancellationToken(canceled: true)));
        Assert.Empty(conn.Commands);
    }

    [Fact]
    public async Task AFailure_IsWrappedWithTheQuerysName_AndTheLogin()
    {
        var conn = new RecordingConnection { Fails = ("SET LOCK_TIMEOUT", new InvalidOperationException("boom")) };
        var repo = new WaitsRepository(Page) { Connect = () => conn };

        var error = await Assert.ThrowsAsync<WaitStatsException>(() => repo.GetTopWaitTypesAsync());

        Assert.Equal("WaitsRepository.GetTopWaitTypesAsync", error.ErrorTag);
        Assert.Equal("vitals", error.Login);
        Assert.Same(SqlFailure.None, error.Failure);
    }

    // ── What every read starts with ──────────────────────────────────────

    [Fact]
    public async Task AReadOfAPage_WaitsForALock_AsLongAsItMayRun()
    {
        var conn = new RecordingConnection();
        await new WaitsRepository(Page) { Connect = () => conn }.GetTopWaitTypesAsync();

        Assert.StartsWith("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\nSET LOCK_TIMEOUT -1;\n" + SqlScripts.Get("Waits/TopWaitTypes"),
                          conn.Commands[0].Sql, StringComparison.Ordinal);
        Assert.Equal(SqlTarget.DefaultCommandTimeoutSeconds, conn.Commands[0].Timeout);
    }

    [Fact]
    public async Task AReadOfMonitoring_GivesUpOnALockAfterTwoSeconds()
    {
        var conn = new RecordingConnection();
        await new WaitsRepository(Monitoring) { Connect = () => conn }.GetTopWaitTypesAsync();

        Assert.StartsWith("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\nSET LOCK_TIMEOUT 2000;\n", conn.Commands[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutNoLock_OnlyTheLockTimeoutIsSet()
    {
        var conn = new RecordingConnection();
        await new WaitsRepository(Page with { UseNoLock = false }) { Connect = () => conn }.GetTopWaitTypesAsync();

        Assert.StartsWith("SET LOCK_TIMEOUT -1;\n", conn.Commands[0].Sql, StringComparison.Ordinal);
    }

    // ── The server's capabilities ────────────────────────────────────────

    [Fact]
    public async Task Capabilities_AreReadOnce_ForAllOfAConnectionsRepositories()
    {
        var conn = new RecordingConnection { Answer = Server(3, "16.0.4135.4", "VIEW SERVER STATE", "CONNECT SQL") };
        var shared = new ServerCapabilitiesCache();
        var server = new ServerRepository(Page, shared) { Connect = () => conn };
        var memory = new MemoryRepository(Page, shared) { Connect = () => conn };

        var capabilities = await server.GetCapabilitiesAsync();
        await server.IsAzureSqlDatabaseAsync();
        await memory.GetMemoryGrantsAsync();

        Assert.Single(conn.Commands, c => c.Sql.Contains("SERVERPROPERTY('ProductVersion')", StringComparison.Ordinal));
        Assert.Equal((3, 16, AzureKind.None), (capabilities.EngineEdition, capabilities.MajorVersion, capabilities.Azure));
        Assert.True(capabilities.Has("VIEW SERVER STATE"));
        Assert.False(capabilities.Has("ALTER ANY EVENT SESSION"));
    }

    [Theory]
    [InlineData(3, "12.0.6024.0", "Memory/GrantsCompat")]   // SQL Server 2014: no queue_id
    [InlineData(3, "13.0.7037.1", "Memory/GrantsFull")]     // 2016
    [InlineData(5, "12.0.2000.8", "Memory/GrantsFull")]     // Azure SQL Database reports 12, and has them
    [InlineData(8, "12.0.2000.8", "Memory/GrantsFull")]     // Managed Instance too
    public async Task TheMemoryGrantsQuery_IsChosenByTheCapabilities(int edition, string version, string expected)
    {
        var conn = new RecordingConnection { Answer = Server(edition, version) };

        await new MemoryRepository(Page) { Connect = () => conn }.GetMemoryGrantsAsync();

        Assert.Contains(conn.Commands, c => c.Sql.EndsWith(SqlScripts.Get(expected), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(8, false)]
    [InlineData(3, false)]
    public async Task LiveMetrics_ReadsAzureSqlDatabasesCpuItsOwnWay(int edition, bool isAzureSqlDb)
    {
        var conn = new RecordingConnection { Answer = Server(edition, "16.0.1000.6") };

        // No row comes back, so the snapshot fails; the query sent is what's checked.
        await Assert.ThrowsAnyAsync<Exception>(() => new LiveMetricsRepository(Page) { Connect = () => conn }.GetLiveMetricsAsync());

        Assert.Contains(conn.Commands, c => c.Sql.EndsWith(LiveMetricsRepository.LiveMetricsSql(isAzureSqlDb), StringComparison.Ordinal));
    }

    [Fact]
    public async Task PermissionsThatCantBeRead_AreAssumed_SoTheQueryTells()
    {
        var conn = new RecordingConnection
        {
            Answer = Server(3, "16.0.4135.4"),
            Fails  = ("fn_my_permissions", new InvalidOperationException("denied")),
        };

        var capabilities = await new ServerRepository(Page) { Connect = () => conn }.GetCapabilitiesAsync();

        Assert.False(capabilities.PermissionsKnown);
        Assert.True(capabilities.Has("VIEW SERVER STATE"));
    }

    [Fact]
    public async Task AFailedCapabilitiesRead_IsTriedAgain()
    {
        var fail = true;
        var conn = new RecordingConnection
        {
            Answer = sql => fail && sql.Contains("ProductVersion", StringComparison.Ordinal) ? null : Server(3, "16.0.4135.4")(sql),
        };
        var server = new ServerRepository(Page) { Connect = () => conn };

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.GetCapabilitiesAsync());
        fail = false;

        Assert.Equal(3, (await server.GetCapabilitiesAsync()).EngineEdition);
    }

    [Theory]
    [InlineData("16.0.4135.4", 16)]
    [InlineData("12.0.2000.8", 12)]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    public void TheMajorVersion_IsReadFromProductVersion(string? version, int major) =>
        Assert.Equal(major, ServerCapabilities.From(3, version, []).MajorVersion);

    [Fact]
    public void TheEdition_SaysWhichAzure() =>
        Assert.Equal([AzureKind.None, AzureKind.SqlDatabase, AzureKind.ManagedInstance],
                     new[] { 3, 5, 8 }.Select(e => ServerCapabilities.From(e, "16.0", []).Azure));

    // ── The login a GRANT names ──────────────────────────────────────────

    [Fact]
    public void TheLogin_IsTheUserId_OrNoneForAnEmptyString()
    {
        Assert.Equal("vitals", Page.Login);
        Assert.Null(new SqlTarget("").Login);
        Assert.Null(new SqlTarget("this is not a connection string").Login);
    }

    [Fact]
    public void TheLogin_WithWindowsAuthentication_IsTheWindowsUser() =>
        Assert.EndsWith(Environment.UserName, new SqlTarget("Server=sql01;Integrated Security=true").Login, StringComparison.Ordinal);
}
