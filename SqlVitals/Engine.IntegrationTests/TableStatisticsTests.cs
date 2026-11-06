using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>
/// Table Statistics (#242) on a real server: its sizes against sp_spaceused for each shape of table,
/// another database read without connecting to it, a schema-change lock held while it reads, and a
/// login that lacks the permissions.
/// </summary>
[Collection(SqlServerTestGroup.Name)]
public class TableStatisticsTests(SqlServerFixture server)
{
    private const string Other = "SqlVitalsITOther";

    private readonly IWaitStatsRepository _repo = server.NewRepository();

    private static TableSize Named(TableSizeList list, string schema, string table) =>
        list.Tables.Single(t => t.SchemaName == schema && t.TableName == table);

    [SqlServerFact]
    [Covers(nameof(ITableStatisticsRepository.GetTableDatabasesAsync))]
    public async Task Databases_AreTheOnlineOnes_StartingFromTheConnections()
    {
        var databases = await _repo.GetTableDatabasesAsync();

        Assert.True(databases.CanSwitch);
        Assert.Equal(SqlServerFixture.Database, databases.Connected);
        Assert.Contains(SqlServerFixture.Database, databases.Names);
        Assert.Contains(Other, databases.Names);
        Assert.DoesNotContain("SqlVitalsITOffline", databases.Names);
    }

    [SqlServerTheory]
    [InlineData("HeapTable")]
    [InlineData("ClusteredTable")]
    [InlineData("PartitionedTable")]
    [InlineData("ColumnstoreTable")]
    public async Task EachShapeOfTable_HasTheReservedSizeSpSpaceusedSays(string table)
    {
        var sizes = Named(await _repo.GetTableSizesAsync(null), "sizes", table);
        var reservedKb = await SpSpaceusedReservedKbAsync($"sizes.{table}");

        Assert.False(sizes.IsLocked);
        Assert.True(reservedKb == sizes.TotalPages * 8,
                    $"{table}: sp_spaceused says {reservedKb} KB, Table Statistics {sizes.TotalPages * 8} KB. " +
                    await server.DescribeAsync($"""
                        SELECT i.index_id, i.type_desc, p.reserved_page_count, p.used_page_count, p.row_count
                        FROM sys.dm_db_partition_stats p JOIN sys.indexes i ON i.object_id = p.object_id AND i.index_id = p.index_id
                        WHERE p.object_id = OBJECT_ID(N'sizes.{table}')
                        UNION ALL
                        SELECT -1, it.internal_type_desc, p.reserved_page_count, p.used_page_count, p.row_count
                        FROM sys.internal_tables it JOIN sys.dm_db_partition_stats p ON p.object_id = it.object_id
                        WHERE it.parent_object_id = OBJECT_ID(N'sizes.{table}')
                        """));
    }

    [SqlServerFact]
    [Covers(nameof(ITableStatisticsRepository.GetTableSizesAsync))]
    public async Task AClusteredIndex_CountsAsData_AndLobAndOverflowAreIncluded()
    {
        var list = await _repo.GetTableSizesAsync(null);
        var clustered = Named(list, "sizes", "ClusteredTable");
        var heap = Named(list, "sizes", "HeapTable");

        Assert.Equal(300, clustered.RowsInTable);
        Assert.Equal(3000, heap.RowsInTable);
        Assert.True(clustered.IndexPages > 0);
        // 300 rows of 40,000 bytes of LOB alone are over 11 MB: far more than the in-row pages.
        Assert.True(clustered.DataPages * 8 > 11_000, $"{clustered.DataPages} data pages");
        Assert.Equal(list.Tables.OrderByDescending(t => t.TotalPages ?? -1).Select(t => t.TableName), list.Tables.Select(t => t.TableName));
    }

    [SqlServerFact]
    public async Task APartitionedTable_AddsUpItsPartitions()
    {
        var list = await _repo.GetTableIndexesAsync(null, await ObjectIdAsync("sizes.PartitionedTable"));

        Assert.Equal(3000, list.Indexes.Single(i => i.Name == "CX_PartitionedTable").RowsInIndex);
        Assert.Equal(3000, list.Indexes.Single(i => i.Name == "IX_PartitionedTable_Filler").RowsInIndex);
    }

    [SqlServerFact]
    [Covers(nameof(ITableStatisticsRepository.GetTableIndexesAsync))]
    public async Task Indexes_HaveSizesAndUsage_AndWhenTheServerRestarted()
    {
        var list = await _repo.GetTableIndexesAsync(null, await ObjectIdAsync("dbo.Orders"));

        Assert.Empty(list.Failures);
        Assert.False(list.IsLocked);
        Assert.NotNull(list.ServerStarted);
        var unused = list.Indexes.Single(i => i.Name == "IX_Orders_Unused");
        Assert.Equal(0, unused.Reads);
        Assert.True(unused.Writes > 0);
        Assert.True(unused.UsedPages <= unused.ReservedPages);
        Assert.False(unused.IsData);
        Assert.Single(list.Indexes, i => i.IsData);
    }

    [SqlServerFact]
    public async Task AHeap_IsListedAsTheTablesData()
    {
        var list = await _repo.GetTableIndexesAsync(null, await ObjectIdAsync("sizes.HeapTable"));

        var heap = list.Indexes.Single(i => i.IndexId == 0);
        Assert.Null(heap.Name);
        Assert.Equal("HEAP", heap.Type);
        Assert.True(heap.IsData);
    }

    [SqlServerFact]
    public async Task AnotherDatabase_IsRead_WithoutChangingTheConnection()
    {
        var sizes = await _repo.GetTableSizesAsync(Other);

        var table = Assert.Single(sizes.Tables);
        Assert.Equal("OnlyInOther", table.TableName);
        Assert.Empty(sizes.Failures);

        var indexes = await _repo.GetTableIndexesAsync(Other, table.ObjectId);
        Assert.Equal(["IX_OnlyInOther_V", "PK_OnlyInOther"], indexes.Indexes.Select(i => i.Name).Order());
        Assert.Empty(indexes.Failures);

        // The same repository still reads the database it connects to.
        Assert.Equal(SqlServerFixture.Database, (await _repo.GetTableDatabasesAsync()).Connected);
        Assert.Contains((await _repo.GetTableSizesAsync(null)).Tables, t => t.TableName == "Orders");
    }

    [SqlServerTheory]
    [InlineData("ALTER TABLE sizes.LockedTable ADD Extra int NULL")]
    [InlineData("TRUNCATE TABLE sizes.LockedTable")]
    [InlineData("ALTER INDEX ALL ON sizes.LockedTable REBUILD")]
    public async Task ATableUnderASchemaChange_ShowsAsLocked_AndTheOthersLoad(string change)
    {
        var objectId = await ObjectIdAsync("sizes.LockedTable");
        var other = await ObjectIdAsync("dbo.Orders");

        await using var holder = new SqlConnection(server.Target.ConnectionString);
        await holder.OpenAsync();
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (var hold = new SqlCommand(change, holder, transaction))
            await hold.ExecuteNonQueryAsync();

        // Both ways in: the connection's database, and through its name, as the dropdown reads it.
        foreach (var database in new[] { null, SqlServerFixture.Database })
        {
            var clock = Stopwatch.StartNew();
            var list = await _repo.GetTableSizesAsync(database);

            // Doesn't wait out the lock: the list's reads go past it.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"{database} took {clock.Elapsed}");
            Assert.Empty(list.Failures);
            var locked = Named(list, "sizes", "LockedTable");
            Assert.True(locked.IsLocked);
            Assert.Null(locked.TotalPages);
            Assert.Equal(locked, list.Tables[^1]);
            Assert.True(Named(list, "dbo", "Orders").TotalPages > 0);

            // Selected: its index names still read, and it says it's locked.
            var indexes = await _repo.GetTableIndexesAsync(database, objectId);
            Assert.True(indexes.IsLocked);
            Assert.Contains(indexes.Indexes, i => i.Name == "IX_LockedTable_V");
            Assert.All(indexes.Indexes, i => Assert.Null(i.ReservedPages));

            // Another table, selected while this one is locked, reads as usual.
            var others = await _repo.GetTableIndexesAsync(database, other);
            Assert.False(others.IsLocked);
            Assert.Empty(others.Failures);
            Assert.All(others.Indexes, i => Assert.NotNull(i.Writes));
        }

        await transaction.RollbackAsync();
    }

    [SqlServerFact]
    public async Task ALoginWithoutThePermissions_SeesWhatItCan_AndIsToldWhatsMissing()
    {
        var login = $"sqlvitals_ts_{Guid.NewGuid():N}"[..24];
        const string password = "Bare-login-1!";
        var database = server.Target.ConnectionString;
        await ExecuteAsync(database, $"""
            CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;
            CREATE USER [{login}] FOR LOGIN [{login}];
            ALTER ROLE db_datareader ADD MEMBER [{login}];
            """);
        try
        {
            var limited = new TableStatisticsRepository(new SqlTarget(new SqlConnectionStringBuilder(database)
            {
                UserID = login, Password = password, IntegratedSecurity = false, Pooling = false,
            }.ConnectionString));

            var sizes = await limited.GetTableSizesAsync(null);

            // Its names, from what db_datareader can see, without sizes.
            var orders = Named(sizes, "dbo", "Orders");
            Assert.Null(orders.TotalPages);
            Assert.False(orders.IsLocked);
            var missing = sizes.Failures.Single(f => f.What == "table sizes");
            Assert.Contains("VIEW DATABASE", missing.Explanation);
            Assert.Contains("GRANT VIEW DATABASE", missing.FixScript);
            Assert.Contains(login, missing.FixScript);
            Assert.Contains(sizes.Failures, f => f.What == "which tables are locked" && f.Explanation.Contains("VIEW SERVER", StringComparison.Ordinal));

            var indexes = await limited.GetTableIndexesAsync(null, orders.ObjectId);

            Assert.Contains(indexes.Indexes, i => i.Name == "IX_Orders_Unused");
            Assert.All(indexes.Indexes, i => Assert.Null(i.ReservedPages));
            Assert.All(indexes.Indexes, i => Assert.Null(i.Reads));
            Assert.Null(indexes.ServerStarted);
            Assert.Contains(indexes.Failures, f => f.What == "table sizes");
            Assert.Contains(indexes.Failures, f => f.What == "index reads and writes" && f.Explanation.Contains("VIEW SERVER", StringComparison.Ordinal));
        }
        finally
        {
            await ExecuteAsync(database, $"DROP USER [{login}]; DROP LOGIN [{login}];");
        }
    }

    [SqlServerFact]
    public async Task ALoginWithoutAccessToADatabase_IsToldSo()
    {
        var login = $"sqlvitals_ts_{Guid.NewGuid():N}"[..24];
        const string password = "Bare-login-1!";
        var database = server.Target.ConnectionString;
        await ExecuteAsync(database, $"""
            CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;
            CREATE USER [{login}] FOR LOGIN [{login}];
            """);
        try
        {
            var limited = new TableStatisticsRepository(new SqlTarget(new SqlConnectionStringBuilder(database)
            {
                UserID = login, Password = password, IntegratedSecurity = false, Pooling = false,
            }.ConnectionString));

            // HAS_DBACCESS keeps it off the list, so it can only be asked for by name.
            Assert.DoesNotContain(Other, (await limited.GetTableDatabasesAsync()).Names);
            var error = await Assert.ThrowsAsync<WaitStatsException>(() => limited.GetTableSizesAsync(Other));
            Assert.True(error.Failure.IsPermission, error.Message);
        }
        finally
        {
            await ExecuteAsync(database, $"DROP USER [{login}]; DROP LOGIN [{login}];");
        }
    }

    [SqlServerFact]
    public async Task OnAzureSqlDatabase_ThereIsOneDatabase_AndNoSwitching()
    {
        var real = await new ServerRepository(server.Target).GetCapabilitiesAsync();
        var capabilities = new ServerCapabilitiesCache();
        await capabilities.GetAsync(() => Task.FromResult(real with { EngineEdition = ServerCapabilities.AzureSqlDatabaseEdition }), CancellationToken.None);
        var azure = new TableStatisticsRepository(server.Target, capabilities);

        var databases = await azure.GetTableDatabasesAsync();

        Assert.False(databases.CanSwitch);
        Assert.Equal([SqlServerFixture.Database], databases.Names);
        Assert.Contains((await azure.GetTableSizesAsync(null)).Tables, t => t.TableName == "Orders");
    }

    private async Task<long> SpSpaceusedReservedKbAsync(string table)
    {
        await using var conn = new SqlConnection(server.Target.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"EXEC sp_spaceused N'{table}'", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var reserved = reader.GetString(reader.GetOrdinal("reserved"));
        return long.Parse(reserved.Replace(" KB", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
    }

    private async Task<int> ObjectIdAsync(string table)
    {
        await using var conn = new SqlConnection(server.Target.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT OBJECT_ID(N'{table}')", conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
