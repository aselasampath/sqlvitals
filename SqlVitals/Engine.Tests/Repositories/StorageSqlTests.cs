using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

/// <summary>
/// File space, TempDB counters and the top tables (#109). FILEPROPERTY only sees the current
/// database's files, and sys.master_files holds tempdb's startup size, not its size now.
/// </summary>
public class StorageSqlTests
{
    [Fact]
    public void Tempdb_files_are_read_in_tempdbs_own_context()
    {
        foreach (var sql in new[] { TempDbRepository.FilesSql, StorageRepository.TempDbFilesSummarySql })
        {
            Assert.StartsWith("EXEC tempdb.sys.sp_executesql", sql.TrimStart());
            Assert.Contains("FROM sys.database_files", sql);
            Assert.DoesNotContain("sys.master_files", sql);
        }
    }

    [Fact]
    public void Every_databases_used_space_is_read_in_that_databases_context()
    {
        var sql = StorageRepository.DatabaseFilesSql(hasQueryStore: true);

        Assert.Contains("QUOTENAME(@db) + N'.sys.sp_executesql'", sql);
        Assert.Contains("HAS_DBACCESS(name) = 1", sql);
        Assert.Contains("FILEPROPERTY(name, N''SpaceUsed'')", sql);
        // FILEPROPERTY appears only inside the per-database statement.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, "FILEPROPERTY"));
        // A database that couldn't be read shows no used space rather than 0.
        Assert.Contains("CAST(u.used_pages * 8.0 / 1024 AS DECIMAL(18,2))", sql);
        Assert.Contains("ISNULL(u.size_pages, f.size)", sql);
        Assert.Contains("DROP TABLE #used", sql);
    }

    [Fact]
    public void Tempdb_counters_find_the_version_store_counters_where_they_live()
    {
        var sql = TempDbRepository.CountersSql;

        // The version store counters have no instance: requiring instance_name = 'tempdb' on
        // them returned nothing. Active Transactions is per database, under Databases.
        Assert.Contains("object_name LIKE N'%:Transactions%'", sql);
        Assert.Contains("N'Version Store Size (KB)'", sql);
        Assert.Contains("N'Free Space in tempdb (KB)'", sql);
        Assert.Contains("object_name LIKE N'%:Databases%' AND instance_name = N'tempdb'", sql);
        Assert.Contains("counter_name = N'Active Transactions'", sql);
    }

    [Fact]
    public void Tempdb_counters_leave_out_the_rates_which_are_totals_since_startup()
    {
        var sql = TempDbRepository.CountersSql;

        Assert.Contains("cntr_type = 65792", sql);
        Assert.DoesNotContain("rate", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tempdb_sessions_are_user_sessions_whatever_their_id()
    {
        Assert.Contains("s.is_user_process = 1", TempDbRepository.SessionsSql);
        Assert.DoesNotContain("session_id > 50", TempDbRepository.SessionsSql);
    }

    [Fact]
    public void Top_tables_are_one_row_per_table()
    {
        var sql = StorageRepository.TopTablesSql;
        var groupBy = sql[sql.IndexOf("GROUP BY", StringComparison.Ordinal)..];

        // Grouping by p.rows split a partitioned table, or one with a filtered index, into rows.
        Assert.DoesNotContain("p.rows", groupBy);
        Assert.Contains("t.object_id", groupBy);
        Assert.Contains("p2.index_id IN (0, 1)", sql);
    }
}
