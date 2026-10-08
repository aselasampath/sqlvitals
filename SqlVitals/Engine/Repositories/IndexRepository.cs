using System.Globalization;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Repositories;

/// <summary>Indexes and statistics: missing and unused indexes, usage, fragmentation and stale statistics. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class IndexRepository(IConfiguration configuration) : BaseRepository(configuration), IIndexRepository
{
    // ── 11. Index health ──────────────────────────────────────────────
    public async Task<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)> GetIndexHealthAsync()
    {
        const string missingSql = """
            SELECT TOP 25
                DB_NAME(mid.database_id) AS DatabaseName,
                OBJECT_SCHEMA_NAME(mid.object_id,mid.database_id) AS SchemaName,
                OBJECT_NAME(mid.object_id,mid.database_id) AS TableName,
                mid.equality_columns AS EqualityColumns,
                mid.inequality_columns AS InequalityColumns,
                mid.included_columns AS IncludedColumns,
                migs.unique_compiles AS UniqueCompiles,
                migs.user_seeks AS UserSeeks, migs.user_scans AS UserScans,
                migs.last_user_seek AS LastUserSeek, migs.last_user_scan AS LastUserScan,
                migs.avg_total_user_cost AS AvgTotalUserCost,
                migs.avg_user_impact AS AvgUserImpact,
                CAST((migs.user_seeks+migs.user_scans)*migs.avg_total_user_cost*(migs.avg_user_impact/100.0) AS DECIMAL(18,2)) AS ImpactScore,
                CASE WHEN migs.avg_user_impact>=80 THEN 'CRITICAL' WHEN migs.avg_user_impact>=50 THEN 'WARNING' ELSE 'INFO' END AS Severity,
                'CREATE NONCLUSTERED INDEX [IX_'+OBJECT_NAME(mid.object_id,mid.database_id)+'_Missing'+CAST(mid.index_handle AS VARCHAR)+'] ON '+
                QUOTENAME(OBJECT_SCHEMA_NAME(mid.object_id,mid.database_id))+'.'+QUOTENAME(OBJECT_NAME(mid.object_id,mid.database_id))+' ('+
                ISNULL(mid.equality_columns,'')+
                CASE WHEN mid.equality_columns IS NOT NULL AND mid.inequality_columns IS NOT NULL THEN ',' ELSE '' END+
                ISNULL(mid.inequality_columns,'')+')'+ 
                CASE WHEN mid.included_columns IS NOT NULL THEN ' INCLUDE ('+mid.included_columns+')' ELSE '' END+';' AS CreateIndexStatement,
                GETDATE() AS CaptureTime
            FROM sys.dm_db_missing_index_details mid
            JOIN sys.dm_db_missing_index_groups mig ON mid.index_handle=mig.index_handle
            JOIN sys.dm_db_missing_index_group_stats migs ON mig.index_group_handle=migs.group_handle
            WHERE mid.database_id=DB_ID()
            ORDER BY ImpactScore DESC
            """;

        const string unusedSql = """
            SELECT DB_NAME() AS DatabaseName,
                OBJECT_SCHEMA_NAME(ix.object_id) AS SchemaName,
                OBJECT_NAME(ix.object_id) AS TableName,
                ix.name AS IndexName, ix.type_desc AS IndexType,
                CAST(ix.is_unique AS BIT) AS IsUnique,
                CAST(ix.is_primary_key AS BIT) AS IsPrimaryKey,
                CAST(ix.is_unique_constraint AS BIT) AS IsUniqueConstraint,
                ISNULL(ius.user_seeks,0) AS UserSeeks,
                ISNULL(ius.user_scans,0) AS UserScans,
                ISNULL(ius.user_lookups,0) AS UserLookups,
                ISNULL(ius.user_updates,0) AS UserUpdates,
                ius.last_user_seek AS LastUserSeek,
                ius.last_user_scan AS LastUserScan,
                ius.last_user_update AS LastUserUpdate,
                ISNULL(ius.user_seeks+ius.user_scans+ius.user_lookups,0) AS TotalReads,
                SUM(p.rows) AS TableRows,
                GETDATE() AS CaptureTime
            FROM sys.indexes ix
            JOIN sys.objects o ON ix.object_id=o.object_id
            LEFT JOIN sys.dm_db_index_usage_stats ius
                ON ix.object_id=ius.object_id AND ix.index_id=ius.index_id AND ius.database_id=DB_ID()
            LEFT JOIN sys.partitions p ON ix.object_id=p.object_id AND ix.index_id=p.index_id
            WHERE o.type='U' AND ix.type>0 AND ix.is_primary_key=0 AND ix.is_unique_constraint=0
              AND ISNULL(ius.user_seeks+ius.user_scans+ius.user_lookups,0)=0
              AND ISNULL(ius.user_updates,0)>0
            GROUP BY ix.object_id,ix.name,ix.type_desc,ix.is_unique,ix.is_primary_key,ix.is_unique_constraint,
                ius.user_seeks,ius.user_scans,ius.user_lookups,ius.user_updates,
                ius.last_user_seek,ius.last_user_scan,ius.last_user_update
            ORDER BY ISNULL(ius.user_updates,0) DESC
            """;

        using var conn = CreateConnection();
        var missing = (await Q(conn, missingSql)).Select(r => new MissingIndex(
            (string)r.DatabaseName, (string)r.SchemaName, (string)r.TableName,
            (string?)r.EqualityColumns, (string?)r.InequalityColumns, (string?)r.IncludedColumns,
            (long)r.UniqueCompiles, (long)r.UserSeeks, (long)r.UserScans,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan,
            (double)r.AvgTotalUserCost, (double)r.AvgUserImpact, (double)r.ImpactScore,
            (string)r.Severity, (string)r.CreateIndexStatement, (DateTime)r.CaptureTime));

        var unused = (await Q(conn, unusedSql)).Select(r => new UnusedIndex(
            (string)r.DatabaseName, (string)r.SchemaName, (string)r.TableName,
            (string)r.IndexName, (string)r.IndexType,
            (bool)r.IsUnique, (bool)r.IsPrimaryKey, (bool)r.IsUniqueConstraint,
            (long)r.UserSeeks, (long)r.UserScans, (long)r.UserLookups, (long)r.UserUpdates,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan, (DateTime?)r.LastUserUpdate,
            (long)r.TotalReads, (long)r.TableRows, (DateTime)r.CaptureTime));

        return (missing, unused);
    }

    // ── 15. Index usage patterns ──────────────────────────────────────
    public async Task<IEnumerable<IndexUsagePattern>> GetIndexUsagePatternsAsync()
    {
        const string sql = """
            SELECT OBJECT_NAME(i.object_id) AS TableName,
                i.name AS IndexName, i.type_desc AS IndexType,
                ISNULL(s.user_seeks,0)   AS UserSeeks,
                ISNULL(s.user_scans,0)   AS UserScans,
                ISNULL(s.user_lookups,0) AS UserLookups,
                ISNULL(s.user_updates,0) AS UserUpdates,
                s.last_user_seek   AS LastUserSeek,
                s.last_user_scan   AS LastUserScan,
                s.last_user_lookup AS LastUserLookup,
                s.last_user_update AS LastUserUpdate,
                CASE
                    WHEN ISNULL(s.user_seeks,0)+ISNULL(s.user_scans,0)+ISNULL(s.user_lookups,0) = 0 THEN 'Unused'
                    WHEN ISNULL(s.user_scans,0) > ISNULL(s.user_seeks,0) THEN 'Mostly Scanned - Review'
                    ELSE 'Healthy'
                END AS IndexHealth,
                DB_NAME(DB_ID()) AS DatabaseName, GETDATE() AS CaptureTime
            FROM sys.indexes i
            JOIN sys.objects o ON i.object_id = o.object_id
            LEFT JOIN sys.dm_db_index_usage_stats s
                ON i.object_id = s.object_id AND i.index_id = s.index_id AND s.database_id = DB_ID()
            WHERE o.type = 'U' AND i.type > 0
            ORDER BY ISNULL(s.user_seeks,0)+ISNULL(s.user_scans,0)+ISNULL(s.user_lookups,0) DESC
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r => new IndexUsagePattern(
            (string)r.TableName, (string?)r.IndexName, (string)r.IndexType,
            (long)r.UserSeeks, (long)r.UserScans, (long)r.UserLookups, (long)r.UserUpdates,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan,
            (DateTime?)r.LastUserLookup, (DateTime?)r.LastUserUpdate,
            (string)r.IndexHealth, (string)r.DatabaseName, (DateTime)r.CaptureTime));
    }

    // ── 16. Index fragmentation ───────────────────────────────────────
    public async Task<IEnumerable<IndexFragmentation>> GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000)
    {
        const string sql = """
            SELECT SCHEMA_NAME(o.schema_id) AS SchemaName,
                OBJECT_NAME(ips.object_id) AS TableName, i.name AS IndexName,
                ips.index_type_desc AS IndexType,
                ips.avg_fragmentation_in_percent AS FragmentationPercent,
                ips.page_count AS PageCount,
                ips.avg_page_space_used_in_percent AS AvgPageSpaceUsed,
                ips.record_count AS RecordCount,
                DB_NAME(ips.database_id) AS DatabaseName,
                CASE
                    WHEN ips.avg_fragmentation_in_percent < 10 THEN 'No Action Needed'
                    WHEN ips.avg_fragmentation_in_percent < 30 THEN 'Reorganize'
                    ELSE 'Rebuild'
                END AS RecommendedAction,
                GETDATE() AS CaptureTime
            FROM sys.dm_db_index_physical_stats(DB_ID(),NULL,NULL,NULL,'LIMITED') ips
            INNER JOIN sys.indexes i  ON ips.object_id = i.object_id AND ips.index_id = i.index_id
            INNER JOIN sys.objects o  ON ips.object_id = o.object_id
            WHERE o.type = 'U'
              AND ips.avg_fragmentation_in_percent >= @MinFrag
              AND ips.page_count >= @MinPageCount
            ORDER BY ips.avg_fragmentation_in_percent DESC
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql, new { MinFrag = minFragmentationPercent, MinPageCount = minPageCount })).Select(r =>
        {
            var row = (IDictionary<string, object>)r;
            object? Get(string k) => row.TryGetValue(k, out var v) && v is not DBNull ? v : null;
            return new IndexFragmentation(
                (string)r.SchemaName, (string)r.TableName, (string?)r.IndexName, (string)r.IndexType,
                Convert.ToDouble(Get("FragmentationPercent") ?? 0.0, CultureInfo.InvariantCulture),
                Convert.ToInt64(Get("PageCount") ?? 0L, CultureInfo.InvariantCulture),
                Convert.ToDouble(Get("AvgPageSpaceUsed") ?? 0.0, CultureInfo.InvariantCulture),
                Convert.ToInt64(Get("RecordCount") ?? 0L, CultureInfo.InvariantCulture),
                (string)r.DatabaseName, (string)r.RecommendedAction, (DateTime)r.CaptureTime);
        });
    }

    // ── 18. Stale statistics ──────────────────────────────────────────
    // A statistic built on an empty table and never updated has no date and no row counts, so
    // those read as null. sys.stats.is_incremental is SQL Server 2014+; before that it is 0.
    internal static string StaleStatisticsSql(bool hasIncremental) =>
        StaleStatisticsSqlTemplate.Replace("/*INCREMENTAL*/", hasIncremental ? "s.is_incremental" : "CAST(0 AS BIT)");

    public async Task<IEnumerable<StaleStatistic>> GetStaleStatisticsAsync()
    {
        using var conn = CreateConnection();
        var sql = StaleStatisticsSql(await HasColumnAsync(conn, "sys.stats", "is_incremental"));
        return (await Q(conn, sql)).Select(r => new StaleStatistic(
            (string)r.SchemaName, (string)r.TableName, (string)r.StatisticsName,
            (DateTime?)r.LastUpdated, (int?)r.DaysOld,
            (long?)r.RowsInTable, (long?)r.RowsSampled, (double?)r.SamplePct ?? 0,
            (long)r.ModificationsSinceLastUpdate,
            (bool)r.IsIncremental, (bool)r.AutoCreated, (bool)r.UserCreated,
            (string)r.DatabaseName, (DateTime)r.CaptureTime));
    }

    private const string StaleStatisticsSqlTemplate = """
            SELECT OBJECT_SCHEMA_NAME(s.object_id) AS SchemaName,
                OBJECT_NAME(s.object_id) AS TableName, s.name AS StatisticsName,
                STATS_DATE(s.object_id,s.stats_id) AS LastUpdated,
                DATEDIFF(DAY,STATS_DATE(s.object_id,s.stats_id),GETDATE()) AS DaysOld,
                sp.rows AS RowsInTable, sp.rows_sampled AS RowsSampled,
                CAST(sp.rows_sampled*100.0/NULLIF(sp.rows,0) AS DECIMAL(10,1)) AS SamplePct,
                sp.modification_counter AS ModificationsSinceLastUpdate,
                /*INCREMENTAL*/ AS IsIncremental, s.auto_created AS AutoCreated,
                s.user_created AS UserCreated, DB_NAME() AS DatabaseName,
                GETDATE() AS CaptureTime
            FROM sys.stats s
            CROSS APPLY sys.dm_db_stats_properties(s.object_id,s.stats_id) sp
            WHERE OBJECTPROPERTY(s.object_id,'IsUserTable')=1 AND sp.modification_counter>0
            ORDER BY sp.modification_counter DESC
            """;
}
