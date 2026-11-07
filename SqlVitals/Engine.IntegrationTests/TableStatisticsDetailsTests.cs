using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>
/// Table Statistics' details (#243) on a real server: histogram keys of each type, statistics with
/// nothing in them, a name that takes some quoting, another database, a schema-change lock held while
/// it reads, and logins that may not read statistics.
/// </summary>
[Collection(SqlServerTestGroup.Name)]
public class TableStatisticsDetailsTests(SqlServerFixture server)
{
    private const string Other = "SqlVitalsITOther";
    private const string OddTable = "stats.[Odd ]]name's table]";
    private const string OddStatistics = "IX odd ]'s.stat";

    private readonly IWaitStatsRepository _repo = server.NewRepository();

    private async Task<(int ObjectId, int StatsId)> FindAsync(string table, string statistics, string? database = null)
    {
        var objectId = await ObjectIdAsync(table, database);
        var list = await _repo.GetTableStatisticsAsync(database, objectId);
        return (objectId, list.Statistics.Single(s => s.Name == statistics).StatsId);
    }

    private async Task<StatisticsDetails> DetailsAsync(string table, string statistics)
    {
        var (objectId, statsId) = await FindAsync(table, statistics);
        return await _repo.GetStatisticsDetailsAsync(null, objectId, statsId);
    }

    // ── Histogram keys ────────────────────────────────────────────────

    [SqlServerTheory]
    [InlineData("S_KInt", "1234")]
    [InlineData("S_KBigint", "9007199254740993")]
    [InlineData("S_KDecimal", "1234567890123456789012345678.0123456789")]
    [InlineData("S_KDate", "2026-01-15")]
    [InlineData("S_KDatetime", "2026-01-15 13:45:30.123")]
    [InlineData("S_KSmalldatetime", "2026-01-15 13:46:00.000")]
    [InlineData("S_KDatetime2", "2026-01-15 13:45:30.1234567")]
    [InlineData("S_KDatetimeoffset", "2026-01-15 13:45:30.1234567 +05:30")]
    [InlineData("S_KTime", "13:45:30.1234567")]
    [InlineData("S_KVarchar", "a varchar key")]
    [InlineData("S_KNvarchar", "ключ nvarchar")]
    [InlineData("S_KGuid", "6F9619FF-8B86-D011-B42D-00C04FC964FF")]
    [InlineData("S_KVarbinary", "0x0A0B0C")]
    [InlineData("S_KBinary", "0x0D0E")]
    [InlineData("S_KBit", "1")]
    [InlineData("S_KFloat", "0.1")]
    [InlineData("S_KReal", "1.5")]
    [InlineData("S_KMoney", "12345.6789")]
    [InlineData("S_KEmpty", "")]
    public async Task EachKeyType_ReadsAsText(string statistics, string key)
    {
        var details = await DetailsAsync("stats.KeyTypes", statistics);

        Assert.Empty(details.Failures);
        var step = Assert.Single(details.Histogram);
        Assert.Equal(key, step.Key);
        Assert.False(step.IsNullStep);
        Assert.Equal(1, step.EqRows);
    }

    [SqlServerFact]
    [Covers(nameof(ITableStatisticsRepository.GetStatisticsDetailsAsync))]
    public async Task AFullHistogram_HasItsStepsInOrder_TheNullStepAndADensity()
    {
        var details = await DetailsAsync("stats.Steps", "S_Steps");

        Assert.Empty(details.Failures);
        Assert.False(details.IsLocked);
        var properties = Assert.IsType<StatisticsProperties>(details.Properties);
        Assert.Equal(properties.Rows, properties.RowsSampled);
        Assert.Equal(100, properties.SamplePct);
        Assert.Equal(properties.Steps, details.Histogram.Count);
        Assert.True(details.Histogram.Count == 201,
            $"{details.Histogram.Count} steps. " + await server.DescribeAsync("SELECT COUNT(*) AS rows_, COUNT(K) AS keyed, COUNT(DISTINCT K) AS distinct_ FROM stats.Steps"));
        Assert.Equal(Enumerable.Range(1, details.Histogram.Count), details.Histogram.Select(s => s.Number));

        var nulls = Assert.Single(details.Histogram, s => s.IsNullStep);
        Assert.Equal(1, nulls.Number);
        Assert.Equal(100, nulls.EqRows);
        Assert.Equal(properties.Rows, details.Histogram.Sum(s => s.TotalRows), 0);
        Assert.Contains(details.Histogram, s => s.RangeRows > 0 && s.DistinctRangeRows > 0 && s.AvgRangeRows > 0);

        var density = Assert.Single(details.Density);
        Assert.Equal("K", density.Columns);
        // The 1,024 keys, and NULL.
        Assert.Equal(1025, density.DistinctValues!.Value, 0);
        Assert.True(density.AverageLength > 0);
        Assert.NotNull(StatisticsFindings.UnknownEstimate(details));

        var server130 = await server.DescribeAsync("SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID()");
        Assert.Contains($"={details.CompatibilityLevel}", server130, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task ANonclusteredIndex_ListsItsClusteringKeyInTheDensityVector()
    {
        var details = await DetailsAsync("dbo.Orders", "IX_Orders_CustomerCode");

        Assert.Equal(["CustomerCode", "CustomerCode, OrderId"], details.Density.Select(d => d.Columns));
        Assert.Equal(500, details.Density[0].DistinctValues!.Value, 0);
    }

    // ── Statistics with nothing in them ───────────────────────────────

    [SqlServerTheory]
    [InlineData("stats.EmptyTable", "PK_EmptyTable")]
    [InlineData("stats.EmptyTable", "S_EmptyTable_V")]
    [InlineData("stats.NeverBuilt", "IX_NeverBuilt_V")]
    public async Task StatisticsWithNoHistogramYet_ReadEmpty_WithoutAFailure(string table, string statistics)
    {
        var details = await DetailsAsync(table, statistics);

        Assert.True(details.Failures.Count == 0, string.Join("; ", details.Failures));
        Assert.Empty(details.Histogram);
        Assert.False(details.IsLocked);
        Assert.Null(StatisticsFindings.Assess(details).Rating);
    }

    [SqlServerTheory]
    [InlineData("sizes.ColumnstoreTable", "CCI_ColumnstoreTable")]
    [InlineData("stats.NoHistogram", "NCCI_NoHistogram")]
    public async Task AnIndexWithNoHistogram_IsListed_AndReadsEmpty(string table, string statistics)
    {
        var details = await DetailsAsync(table, statistics);

        Assert.Empty(details.Histogram);
        Assert.Null(details.Properties);
        Assert.True(details.Failures.Count == 0, string.Join("; ", details.Failures));
    }

    [SqlServerFact]
    public async Task IndexesThatNeverHaveAHistogram_DontCountAsNeverBuilt()
    {
        var batch = await _repo.GetTableStatisticsHealthAsync(null, [await ObjectIdAsync("stats.NoHistogram"), await ObjectIdAsync("stats.ColumnOnly")]);

        // Beside the primary key's rows, one of them reading as never built would make the table Fair.
        var special = batch.Tables[0];
        Assert.True(special.Rating == StatisticsRating.Good, $"{special}. " + await server.DescribeAsync("""
            SELECT st.name, i.type, p.last_updated, p.rows FROM sys.stats st
            LEFT JOIN sys.indexes i ON i.object_id = st.object_id AND i.index_id = st.stats_id
            OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) p
            WHERE st.object_id = OBJECT_ID(N'stats.NoHistogram')
            """));

        // A clustered columnstore alone has nothing to rate.
        Assert.Equal(StatisticsHealthState.NoStatistics, batch.Tables[1].State);
    }

    // ── The list ──────────────────────────────────────────────────────

    [SqlServerFact]
    [Covers(nameof(ITableStatisticsRepository.GetTableStatisticsAsync))]
    public async Task TheList_StartsOnTheClusteredIndex_ThenGoesByName()
    {
        var list = await _repo.GetTableStatisticsAsync(null, await ObjectIdAsync("dbo.Orders"));

        Assert.False(list.IsLocked);
        Assert.Equal("PK_Orders", list.Statistics[0].Name);
        Assert.True(list.Statistics[0].IsClustered);
        Assert.Equal("OrderId", list.Statistics[0].LeadingColumn);
        Assert.Equal(["IX_Orders_CustomerCode", "IX_Orders_Unused", "ST_Orders_Amount"], list.Statistics.Select(s => s.Name).Where(n => !n.StartsWith("_WA_Sys", StringComparison.Ordinal)).Skip(1));

        var column = list.Statistics.Single(s => s.Name == "ST_Orders_Amount");
        Assert.False(column.IsIndex);
        Assert.False(column.IsClustered);
        Assert.Equal("Amount", column.LeadingColumn);
        Assert.True(list.Statistics.Single(s => s.Name == "IX_Orders_Unused").IsIndex);
    }

    [SqlServerFact]
    public async Task TheList_MarksFilteredStatistics_AndLeavesOutHypotheticalIndexes()
    {
        var steps = await _repo.GetTableStatisticsAsync(null, await ObjectIdAsync("stats.Steps"));
        Assert.True(steps.Statistics.Single(s => s.Name == "S_Steps_Filtered").IsFiltered);
        Assert.False(steps.Statistics.Single(s => s.Name == "S_Steps").IsFiltered);

        var noHistogram = await _repo.GetTableStatisticsAsync(null, await ObjectIdAsync("stats.NoHistogram"));
        Assert.DoesNotContain(noHistogram.Statistics, s => s.Name == "IX_NoHistogram_Hypothetical");
        Assert.Contains(noHistogram.Statistics, s => s.Name == "NCCI_NoHistogram");

        // XML and spatial indexes have no statistics of their own in the catalog.
        Assert.DoesNotContain(noHistogram.Statistics, s => s.Name is "PX_NoHistogram" or "SX_NoHistogram");
        Assert.Equal("PK_NoHistogram", noHistogram.Statistics[0].Name);

        var columnstore = await _repo.GetTableStatisticsAsync(null, await ObjectIdAsync("sizes.ColumnstoreTable"));
        Assert.True(columnstore.Statistics.Single(s => s.Name == "CCI_ColumnstoreTable").IsClustered);
        Assert.True(columnstore.Statistics.Single(s => s.Name == "CCI_ColumnstoreTable").IsColumnstore);
    }

    [SqlServerFact]
    public async Task ANameThatTakesQuoting_IsRead_AllTheWay()
    {
        var (objectId, statsId) = await FindAsync(OddTable, OddStatistics);

        var details = await _repo.GetStatisticsDetailsAsync(null, objectId, statsId);

        Assert.True(details.Failures.Count == 0, string.Join("; ", details.Failures));
        Assert.Equal(3, details.Properties?.Rows);
        Assert.NotEmpty(details.Histogram);
        Assert.Equal("V ]'s", details.Density[0].Columns);
        var health = await _repo.GetTableStatisticsHealthAsync(null, [objectId]);
        Assert.Equal(StatisticsHealthState.Rated, Assert.Single(health.Tables).State);
    }

    // ── Health ────────────────────────────────────────────────────────

    [SqlServerFact]
    [Covers(nameof(ITableStatisticsRepository.GetTableStatisticsHealthAsync))]
    public async Task Health_RatesEachTable_InTheOrderAsked()
    {
        var stale = await ObjectIdAsync("stats.Stale");
        var steps = await ObjectIdAsync("stats.Steps");
        var mixed = await ObjectIdAsync("stats.NeverBuilt");
        var noStatistics = await ObjectIdAsync("stats.NoIndex");

        var batch = await _repo.GetTableStatisticsHealthAsync(null, [stale, steps, mixed, noStatistics]);

        Assert.Empty(batch.Failures);
        Assert.Equal([stale, steps, mixed, noStatistics], batch.Tables.Select(t => t.ObjectId));

        var past = batch.Tables[0];
        Assert.Equal(StatisticsHealthState.Rated, past.State);
        Assert.True(past.Rating == StatisticsRating.Poor, past + " " + await server.DescribeAsync("SELECT st.name, p.* FROM sys.stats st OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) p WHERE st.object_id = OBJECT_ID(N'stats.Stale')"));
        Assert.Equal(90, past.MostChangedPct);
        Assert.NotNull(past.OldestUpdate);

        Assert.Equal(StatisticsRating.Good, batch.Tables[1].Rating);
        Assert.True(batch.Tables[2].Rating == StatisticsRating.Fair, batch.Tables[2] + " " + await server.DescribeAsync("SELECT st.name, p.* FROM sys.stats st OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) p WHERE st.object_id = OBJECT_ID(N'stats.NeverBuilt')"));
        Assert.Equal(StatisticsHealthState.NoStatistics, batch.Tables[3].State);
    }

    [SqlServerFact]
    public async Task Health_WithNoTables_IsEmpty()
    {
        var batch = await _repo.GetTableStatisticsHealthAsync(null, []);

        Assert.Empty(batch.Tables);
        Assert.Empty(batch.Failures);
    }

    [SqlServerFact]
    public async Task Cancelling_FlowsOut_BeforeTheNextTable()
    {
        var id = await ObjectIdAsync("stats.Stale");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _repo.GetTableStatisticsHealthAsync(null, [id], cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _repo.GetTableStatisticsAsync(null, id, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _repo.GetStatisticsDetailsAsync(null, id, 1, cancelled.Token));
    }

    // ── Another database ──────────────────────────────────────────────

    [SqlServerFact]
    public async Task AnotherDatabase_IsRead_WithoutConnectingToIt_AtItsOwnCompatibilityLevel()
    {
        var objectId = await ObjectIdAsync("dbo.OnlyInOther", Other);

        var list = await _repo.GetTableStatisticsAsync(Other, objectId);
        Assert.Equal(["PK_OnlyInOther", "IX_OnlyInOther_V"], list.Statistics.Select(s => s.Name));

        var details = await _repo.GetStatisticsDetailsAsync(Other, objectId, list.Statistics[0].StatsId);
        Assert.Empty(details.Failures);
        Assert.Equal(120, details.CompatibilityLevel);
        Assert.Equal(1, details.Properties?.Rows);
        Assert.Equal("1", Assert.Single(details.Histogram).Key);
        Assert.Equal("Id", Assert.Single(details.Density).Columns);

        var batch = await _repo.GetTableStatisticsHealthAsync(Other, [objectId]);
        Assert.Equal(StatisticsRating.Good, Assert.Single(batch.Tables).Rating);
        Assert.Empty(batch.Failures);
    }

    // ── A schema-change lock ──────────────────────────────────────────

    [SqlServerTheory]
    [InlineData("ALTER TABLE sizes.LockedTable ADD Extra int NULL")]
    [InlineData("TRUNCATE TABLE sizes.LockedTable")]
    [InlineData("ALTER INDEX ALL ON sizes.LockedTable REBUILD")]
    public async Task ALockedTable_IsLocked_AndTheOthersAreRated(string change)
    {
        var locked = await ObjectIdAsync("sizes.LockedTable");
        var (other, otherStats) = await FindAsync("stats.Stale", "S_Stale_K");
        var lockedStats = (await _repo.GetTableStatisticsAsync(null, locked)).Statistics[0].StatsId;

        await using var holder = new SqlConnection(server.Target.ConnectionString);
        await holder.OpenAsync();
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (var hold = new SqlCommand(change, holder, transaction))
            await hold.ExecuteNonQueryAsync();

        foreach (var database in new[] { null, SqlServerFixture.Database })
        {
            var clock = Stopwatch.StartNew();
            var batch = await _repo.GetTableStatisticsHealthAsync(database, [locked, other, locked]);

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"Health took {clock.Elapsed}");
            Assert.Empty(batch.Failures);
            Assert.Equal(StatisticsHealthState.Locked, batch.Tables[0].State);
            Assert.Null(batch.Tables[0].Rating);
            Assert.Equal(StatisticsHealthState.Rated, batch.Tables[1].State);
            Assert.Equal(StatisticsHealthState.Locked, batch.Tables[2].State);

            // Its names still read, and it says it's locked.
            clock.Restart();
            var list = await _repo.GetTableStatisticsAsync(database, locked);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"The list took {clock.Elapsed}");
            Assert.True(list.IsLocked);
            Assert.Contains(list.Statistics, s => s.Name == "IX_LockedTable_V" && s.LeadingColumn == "V");

            // The details say so too, having waited only for the first part.
            clock.Restart();
            var details = await _repo.GetStatisticsDetailsAsync(database, locked, lockedStats);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"The details took {clock.Elapsed}");
            Assert.True(details.IsLocked);
            Assert.Empty(details.Failures);
            Assert.Null(details.Properties);
            Assert.Empty(details.Histogram);
            Assert.Empty(details.Density);

            // Another table, selected while this one is locked, reads as usual.
            var others = await _repo.GetStatisticsDetailsAsync(database, other, otherStats);
            Assert.False(others.IsLocked);
            Assert.Empty(others.Failures);
            Assert.NotEmpty(others.Histogram);
            Assert.NotEmpty(others.Density);
        }

        await transaction.RollbackAsync();
    }

    // ── Logins that may not read statistics ───────────────────────────

    [SqlServerFact]
    public async Task ALoginWithoutSelect_IsToldWhichPermission_NotThatTheyWereNeverBuilt()
    {
        await AsLoginAsync("GRANT VIEW DEFINITION TO {0}; GRANT VIEW DATABASE STATE TO {0};", async (limited, login) =>
        {
            var orders = await ObjectIdAsync("dbo.Orders");
            var stale = await ObjectIdAsync("stats.Stale");
            var empty = await ObjectIdAsync("stats.NoIndex");

            var batch = await limited.GetTableStatisticsHealthAsync(null, [orders, stale, empty]);

            Assert.Equal(StatisticsHealthState.NotReadable, batch.Tables[0].State);
            Assert.Equal(StatisticsHealthState.NotReadable, batch.Tables[1].State);
            Assert.Equal(StatisticsHealthState.NoStatistics, batch.Tables[2].State);

            // One failure for the batch, naming the permission and how to give it.
            var failure = Assert.Single(batch.Failures);
            Assert.Contains("SELECT", failure.Explanation, StringComparison.Ordinal);
            Assert.Contains("db_ddladmin", failure.Explanation, StringComparison.Ordinal);
            Assert.Contains($"USE [{SqlServerFixture.Database}]", failure.FixScript, StringComparison.Ordinal);
            Assert.Contains($"GRANT SELECT TO [{login}]", failure.FixScript, StringComparison.Ordinal);

            // The sizes are a separate read, and still there.
            var sizes = await limited.GetTableSizesAsync(null);
            Assert.True(sizes.Tables.Single(t => t.ObjectId == orders).TotalPages > 0);
            Assert.DoesNotContain(sizes.Failures, f => f.What == TableReadFailure.Sizes);

            // The details say the same, for the one table, and don't read as an empty statistics object.
            var list = await limited.GetTableStatisticsAsync(null, orders);
            var details = await limited.GetStatisticsDetailsAsync(null, orders, list.Statistics[0].StatsId);
            Assert.Null(details.Properties);
            Assert.Empty(details.Histogram);
            var denied = Assert.Single(details.Failures);
            Assert.Contains("SELECT", denied.Explanation, StringComparison.Ordinal);
            Assert.Contains($"GRANT SELECT ON [dbo].[Orders] TO [{login}]", denied.FixScript, StringComparison.Ordinal);
        });
    }

    [SqlServerFact]
    public async Task ALoginWithoutSelect_FindsALockedTableLocked_AsTheLockIsMetBeforeThePermission()
    {
        await AsLoginAsync("GRANT VIEW DEFINITION TO {0};", async (limited, _) =>
        {
            var locked = await ObjectIdAsync("sizes.LockedTable");
            await using var holder = new SqlConnection(server.Target.ConnectionString);
            await holder.OpenAsync();
            await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync();
            await using (var hold = new SqlCommand("ALTER TABLE sizes.LockedTable ADD Extra int NULL", holder, transaction))
                await hold.ExecuteNonQueryAsync();

            var batch = await limited.GetTableStatisticsHealthAsync(null, [locked]);
            Assert.Equal(StatisticsHealthState.Locked, Assert.Single(batch.Tables).State);
            Assert.Empty(batch.Failures);

            var details = await limited.GetStatisticsDetailsAsync(null, locked, 1);
            Assert.True(details.IsLocked);
            Assert.Empty(details.Failures);

            await transaction.RollbackAsync();
        });
    }

    [SqlServerFact]
    public async Task ALoginWithSelect_ReadsThemAll_AndNeverBuiltStaysNeverBuilt()
    {
        await AsLoginAsync("ALTER ROLE db_datareader ADD MEMBER {0};", async (limited, _) =>
        {
            var orders = await ObjectIdAsync("dbo.Orders");
            var neverBuilt = await ObjectIdAsync("stats.NeverBuilt");

            var batch = await limited.GetTableStatisticsHealthAsync(null, [orders, neverBuilt]);
            Assert.Empty(batch.Failures);
            Assert.All(batch.Tables, t => Assert.Equal(StatisticsHealthState.Rated, t.State));

            var list = await limited.GetTableStatisticsAsync(null, orders);
            var details = await limited.GetStatisticsDetailsAsync(null, orders, list.Statistics[0].StatsId);
            Assert.True(details.Failures.Count == 0, string.Join("; ", details.Failures));
            Assert.NotEmpty(details.Histogram);
            Assert.NotEmpty(details.Density);

            // Without VIEW SERVER STATE the list doesn't know of a lock, and doesn't complain.
            Assert.False(list.IsLocked);
        });
    }

    [SqlServerFact]
    public async Task ALoginWithSelectOnOneColumn_ReadsThatColumnsStatistics_AndIsToldOfTheRest()
    {
        await AsLoginAsync("GRANT VIEW DEFINITION TO {0}; GRANT SELECT ON stats.Stale (Id) TO {0};", async (limited, login) =>
        {
            var (objectId, id) = await FindAsync("stats.Stale", "S_Stale_Id");
            var (_, k) = await FindAsync("stats.Stale", "S_Stale_K");

            var readable = await limited.GetStatisticsDetailsAsync(null, objectId, id);
            Assert.True(readable.Failures.Count == 0, string.Join("; ", readable.Failures));
            Assert.NotEmpty(readable.Histogram);

            var denied = await limited.GetStatisticsDetailsAsync(null, objectId, k);
            Assert.Empty(denied.Histogram);
            Assert.Contains($"GRANT SELECT ON [stats].[Stale] TO [{login}]", Assert.Single(denied.Failures).FixScript, StringComparison.Ordinal);

            var batch = await limited.GetTableStatisticsHealthAsync(null, [objectId]);
            Assert.Equal(StatisticsHealthState.NotReadable, Assert.Single(batch.Tables).State);
        });
    }

    [SqlServerFact]
    public async Task ADdlAdmin_ReadsThem_WithoutSelect()
    {
        await AsLoginAsync("ALTER ROLE db_ddladmin ADD MEMBER {0};", async (limited, _) =>
        {
            var neverBuilt = await ObjectIdAsync("stats.NeverBuilt");
            var (orders, pk) = await FindAsync("dbo.Orders", "PK_Orders");

            var batch = await limited.GetTableStatisticsHealthAsync(null, [orders, neverBuilt]);
            Assert.Empty(batch.Failures);
            Assert.All(batch.Tables, t => Assert.Equal(StatisticsHealthState.Rated, t.State));

            var details = await limited.GetStatisticsDetailsAsync(null, orders, pk);
            Assert.True(details.Failures.Count == 0, string.Join("; ", details.Failures));
            Assert.NotEmpty(details.Histogram);
        });
    }

    // ── Plumbing ──────────────────────────────────────────────────────

    /// <summary>Runs with a repository logged in as a new login, given these grants ({0} is its user), then drops it.</summary>
    private async Task AsLoginAsync(string grants, Func<TableStatisticsRepository, string, Task> test)
    {
        var login = $"sqlvitals_ts_{Guid.NewGuid():N}"[..24];
        const string password = "Bare-login-1!";
        var database = server.Target.ConnectionString;
        await ExecuteAsync(database, $"""
            CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;
            CREATE USER [{login}] FOR LOGIN [{login}];
            {string.Format(System.Globalization.CultureInfo.InvariantCulture, grants, $"[{login}]")}
            """);
        try
        {
            await test(new TableStatisticsRepository(new SqlTarget(new SqlConnectionStringBuilder(database)
            {
                UserID = login, Password = password, IntegratedSecurity = false, Pooling = false,
            }.ConnectionString)), login);
        }
        finally
        {
            await ExecuteAsync(database, $"DROP USER [{login}]; DROP LOGIN [{login}];");
        }
    }

    private async Task<int> ObjectIdAsync(string table, string? database = null)
    {
        await using var conn = new SqlConnection(server.Target.ConnectionString);
        await conn.OpenAsync();
        var name = (database is null ? "" : $"[{database}].") + table;
        await using var cmd = new SqlCommand("SELECT OBJECT_ID(@name)", conn);
        cmd.Parameters.AddWithValue("@name", name);
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
