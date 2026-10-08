using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Database storage and configuration: settings, files, TempDB files and the largest tables. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class StorageRepository(IConfiguration configuration) : BaseRepository(configuration), IStorageRepository
{
    // ── 19. Database storage & configuration ─────────────────────────
    // Detects Azure SQL (EngineEdition 5/8) and switches to compatible queries.
    // On-prem uses sys.master_files for all DBs; Azure uses sys.database_files
    // for the current DB only and skips the cross-DB tempdb query.
    public async Task<(
        IEnumerable<ServerConfigParam> Config,
        IEnumerable<DatabaseFile> Files,
        IEnumerable<TempDbFileSummary> TempDbFiles,
        IEnumerable<TableStorage> TopTables
    )> GetDatabaseStorageAsync()
    {
        // ── Detect engine edition: 5 = Azure SQL DB, 8 = Azure SQL MI ─
        const string sqlEdition = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition";

        // ── A) Server / database-scoped config ────────────────────────
        // On-prem: sys.configurations (server-wide)
        // Azure SQL DB: sys.database_scoped_configurations (DB-level only)
        const string sqlConfigOnPrem = """
            SELECT name                       AS ParameterName,
                   CAST(value_in_use AS BIGINT) AS CurrentValue,
                   CAST(minimum      AS BIGINT) AS MinValue,
                   CAST(maximum      AS BIGINT) AS MaxValue,
                   description                AS Description,
                   CAST(is_dynamic   AS BIT)  AS IsDynamic,
                   CAST(is_advanced  AS BIT)  AS IsAdvanced
            FROM sys.configurations
            WHERE name IN (
                'max degree of parallelism','cost threshold for parallelism',
                'max server memory (MB)','min server memory (MB)',
                'optimize for ad hoc workloads','max worker threads',
                'lightweight pooling','priority boost','fill factor (%)',
                'index create memory (KB)','min memory per query (KB)',
                'query wait (s)','remote query timeout (s)','locks',
                'open objects','user connections','backup compression default',
                'Database Mail XPs','xp_cmdshell'
            )
            ORDER BY name
            """;

        const string sqlConfigAzure = """
            SELECT name                                              AS ParameterName,
                   CASE
                       WHEN ISNUMERIC(CAST(value AS NVARCHAR(256))) = 1
                       THEN CAST(CAST(value AS NVARCHAR(256)) AS BIGINT)
                       ELSE 0
                   END                                              AS CurrentValue,
                   CAST(0  AS BIGINT)                               AS MinValue,
                   CAST(0  AS BIGINT)                               AS MaxValue,
                   CAST(ISNULL(CAST(value AS NVARCHAR(256)),'')
                        AS NVARCHAR(MAX))                           AS Description,
                   CAST(1  AS BIT)                                  AS IsDynamic,
                   CAST(0  AS BIT)                                  AS IsAdvanced
            FROM sys.database_scoped_configurations
            ORDER BY name
            """;

        using var conn = CreateConnection();

        // Detect Azure SQL (EngineEdition 5 = Azure SQL DB, 8 = Azure SQL MI)
        var editionRow = await QFirst(conn, sqlEdition);
        int edition = editionRow != null ? (int)editionRow.Edition : 0;
        bool isAzureSqlDb = edition == 5;   // Managed Instance (8) still has master_files

        // Config
        var configSql = isAzureSqlDb ? sqlConfigAzure : sqlConfigOnPrem;
        var config = (await Q(conn, configSql)).Select(r => new ServerConfigParam(
            (string)r.ParameterName,
            Convert.ToInt64(r.CurrentValue),
            Convert.ToInt64(r.MinValue),
            Convert.ToInt64(r.MaxValue),
            r.Description == null || r.Description is DBNull ? "" : (string)r.Description,
            Convert.ToBoolean(r.IsDynamic),
            Convert.ToBoolean(r.IsAdvanced)));

        // Files
        var filesSql = isAzureSqlDb
            ? DatabaseFilesAzureSql
            : DatabaseFilesSql(await HasColumnAsync(conn, "sys.databases", "is_query_store_on"));
        var files = (await Q(conn, filesSql)).Select(r => new DatabaseFile(
            (string)r.DatabaseName,
            (string)r.State,
            r.RecoveryModel == null || r.RecoveryModel is DBNull ? "" : (string)r.RecoveryModel,
            Convert.ToInt32(r.CompatibilityLevel),
            r.Collation == null || r.Collation is DBNull ? null : (string)r.Collation,
            Convert.ToBoolean(r.AutoShrink),
            Convert.ToBoolean(r.AutoCreateStats),
            Convert.ToBoolean(r.AutoUpdateStats),
            Convert.ToBoolean(r.QueryStoreOn),
            Convert.ToBoolean(r.CDCEnabled),
            Convert.ToBoolean(r.Encrypted),
            (string)r.FileType,
            (string)r.LogicalFileName,
            r.PhysicalPath == null || r.PhysicalPath is DBNull ? "" : (string)r.PhysicalPath,
            Convert.ToDecimal(r.FileSizeMB),
            r.SpaceUsedMB is null or DBNull ? null : Convert.ToDecimal(r.SpaceUsedMB),
            r.FreeSpaceMB is null or DBNull ? null : Convert.ToDecimal(r.FreeSpaceMB),
            Convert.ToDecimal(r.MaxSizeMB),
            (string)r.AutoGrowth));

        // ── C) TempDB — skip on Azure SQL DB
        IEnumerable<TempDbFileSummary> tempFiles = [];
        if (!isAzureSqlDb)
        {
            tempFiles = (await Q(conn, TempDbFilesSummarySql)).Select(r => new TempDbFileSummary(
                (string)r.FileName,
                (string)r.FileType,
                r.PhysicalPath == null || r.PhysicalPath is DBNull ? "" : (string)r.PhysicalPath,
                Convert.ToDecimal(r.SizeMB),
                Convert.ToDecimal(r.UsedMB),
                Convert.ToDecimal(r.FreeMB)));
        }

        // ── D) Top 20 tables by storage (same on both) ────────────────
        var tables = (await Q(conn, TopTablesSql)).Select(r => new TableStorage(
            (string)r.SchemaName,
            (string)r.TableName,
            Convert.ToInt64(r.RowsCount),
            Convert.ToDecimal(r.DataSizeMB),
            Convert.ToDecimal(r.IndexSizeMB),
            Convert.ToDecimal(r.TotalSizeMB),
            Convert.ToDecimal(r.UsedSizeMB),
            Convert.ToDecimal(r.UnusedSizeMB),
            Convert.ToInt32(r.IndexCount),
            (string)r.TableType));

        return (config, files, tempFiles, tables);
    }

    // ── B) Database files ─────────────────────────────────────────────
    // On-prem: sys.databases + sys.master_files (all databases). FILEPROPERTY only sees the
    // current database's files, so each database's used space, and its size now (tempdb's
    // sys.master_files row holds its startup size), is read in that database's own context
    // through its sys.sp_executesql. A database this login can't open shows no used or free
    // space rather than a wrong one. sys.databases.is_query_store_on is SQL Server 2016+.
    internal static string DatabaseFilesSql(bool hasQueryStore) =>
        DatabaseFilesSqlTemplate.Replace("/*QUERY_STORE_ON*/", hasQueryStore ? "d.is_query_store_on" : "0");

    private const string DatabaseFilesSqlTemplate = """
        SET NOCOUNT ON;
        CREATE TABLE #used (database_id INT NOT NULL, file_id INT NOT NULL, size_pages BIGINT NULL, used_pages BIGINT NULL);

        DECLARE @db SYSNAME, @proc NVARCHAR(300);
        DECLARE dbs CURSOR LOCAL FAST_FORWARD FOR
            SELECT name FROM sys.databases WHERE state_desc = N'ONLINE' AND HAS_DBACCESS(name) = 1;
        OPEN dbs;
        FETCH NEXT FROM dbs INTO @db;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @proc = QUOTENAME(@db) + N'.sys.sp_executesql';
            BEGIN TRY
                INSERT #used (database_id, file_id, size_pages, used_pages)
                EXEC @proc N'SELECT DB_ID(), file_id, CAST(size AS BIGINT), CAST(FILEPROPERTY(name, N''SpaceUsed'') AS BIGINT) FROM sys.database_files';
            END TRY
            BEGIN CATCH
            END CATCH;
            FETCH NEXT FROM dbs INTO @db;
        END;
        CLOSE dbs;
        DEALLOCATE dbs;

        SELECT
            d.name                              AS DatabaseName,
            d.state_desc                        AS State,
            d.recovery_model_desc               AS RecoveryModel,
            d.compatibility_level               AS CompatibilityLevel,
            d.collation_name                    AS Collation,
            CAST(d.is_auto_shrink_on       AS BIT) AS AutoShrink,
            CAST(d.is_auto_create_stats_on AS BIT) AS AutoCreateStats,
            CAST(d.is_auto_update_stats_on AS BIT) AS AutoUpdateStats,
            CAST(/*QUERY_STORE_ON*/        AS BIT) AS QueryStoreOn,
            CAST(d.is_cdc_enabled          AS BIT) AS CDCEnabled,
            CAST(d.is_encrypted            AS BIT) AS Encrypted,
            f.type_desc                         AS FileType,
            f.name                              AS LogicalFileName,
            f.physical_name                     AS PhysicalPath,
            CAST(ISNULL(u.size_pages, f.size) * 8.0 / 1024 AS DECIMAL(18,2))                                 AS FileSizeMB,
            CAST(u.used_pages * 8.0 / 1024 AS DECIMAL(18,2))                                                 AS SpaceUsedMB,
            CAST((u.size_pages - u.used_pages) * 8.0 / 1024 AS DECIMAL(18,2))                                AS FreeSpaceMB,
            CASE WHEN f.max_size = -1 THEN CAST(-1 AS DECIMAL(18,2))
                 ELSE CAST(f.max_size * 8.0/1024 AS DECIMAL(18,2)) END                                        AS MaxSizeMB,
            CASE WHEN f.is_percent_growth = 1
                 THEN CAST(f.growth AS VARCHAR(10)) + '%'
                 ELSE CAST(CAST(f.growth AS BIGINT) * 8 / 1024 AS VARCHAR(20)) + ' MB'
            END AS AutoGrowth
        FROM sys.databases d
        JOIN sys.master_files f ON d.database_id = f.database_id
        LEFT JOIN #used u       ON u.database_id = f.database_id AND u.file_id = f.file_id
        WHERE d.state_desc = 'ONLINE'
        ORDER BY d.name, f.type_desc, f.name;

        DROP TABLE #used;
        """;

    // Azure SQL Database: sys.database_files for the current database only, which is the one
    // FILEPROPERTY reads.
    private const string DatabaseFilesAzureSql = """
        SELECT
            DB_NAME()                              AS DatabaseName,
            'ONLINE'                               AS State,
            CAST(DATABASEPROPERTYEX(DB_NAME(),'Recovery')  AS NVARCHAR(60))  AS RecoveryModel,
            CAST(d.compatibility_level             AS INT)  AS CompatibilityLevel,
            CAST(DATABASEPROPERTYEX(DB_NAME(),'Collation') AS NVARCHAR(128)) AS Collation,
            CAST(0 AS BIT)                         AS AutoShrink,
            CAST(1 AS BIT)                         AS AutoCreateStats,
            CAST(1 AS BIT)                         AS AutoUpdateStats,
            CAST(d.is_query_store_on          AS BIT) AS QueryStoreOn,
            CAST(0 AS BIT)                         AS CDCEnabled,
            CAST(0 AS BIT)                         AS Encrypted,
            f.type_desc                            AS FileType,
            f.name                                 AS LogicalFileName,
            ISNULL(f.physical_name,'')             AS PhysicalPath,
            CAST(f.size * 8.0 / 1024 AS DECIMAL(18,2))                                                       AS FileSizeMB,
            CAST(ISNULL(FILEPROPERTY(f.name,'SpaceUsed'),0) * 8.0/1024 AS DECIMAL(18,2))                     AS SpaceUsedMB,
            CAST((f.size - ISNULL(FILEPROPERTY(f.name,'SpaceUsed'),0)) * 8.0/1024 AS DECIMAL(18,2))          AS FreeSpaceMB,
            CASE WHEN f.max_size = -1 THEN CAST(-1 AS DECIMAL(18,2))
                 ELSE CAST(f.max_size * 8.0/1024 AS DECIMAL(18,2)) END                                        AS MaxSizeMB,
            CASE WHEN f.is_percent_growth = 1
                 THEN CAST(f.growth AS VARCHAR(10)) + '%'
                 ELSE CAST(CAST(f.growth AS BIGINT) * 8 / 1024 AS VARCHAR(20)) + ' MB'
            END AS AutoGrowth
        FROM sys.database_files f
        CROSS JOIN (SELECT compatibility_level, is_query_store_on
                    FROM sys.databases WHERE name = DB_NAME()) d
        ORDER BY f.type_desc, f.name
        """;

    // ── C) TempDB files ───────────────────────────────────────────────
    // In tempdb's own context, for FILEPROPERTY. Azure SQL DB doesn't allow it, so it's skipped there.
    internal const string TempDbFilesSummarySql = """
        EXEC tempdb.sys.sp_executesql N'
            SELECT
                name          AS FileName,
                type_desc     AS FileType,
                physical_name AS PhysicalPath,
                CAST(size * 8.0/1024 AS DECIMAL(18,2))                                                      AS SizeMB,
                CAST(ISNULL(FILEPROPERTY(name, ''SpaceUsed''), 0) * 8.0/1024 AS DECIMAL(18,2))              AS UsedMB,
                CAST((size - ISNULL(FILEPROPERTY(name, ''SpaceUsed''), 0)) * 8.0/1024 AS DECIMAL(18,2))     AS FreeMB
            FROM sys.database_files
            ORDER BY type_desc, name;';
        """;

    // ── D) Top 20 tables by storage (same on both) ────────────────────
    // One row per table: sizes add up every partition of every index, and the row count is the
    // heap's or clustered index's, summed over its partitions.
    internal const string TopTablesSql = """
        SELECT TOP 20
            SCHEMA_NAME(t.schema_id)                                                   AS SchemaName,
            t.name                                                                     AS TableName,
            ISNULL(rc.RowsCount, 0)                                                    AS RowsCount,
            CAST(SUM(CASE WHEN i.type <= 1 THEN a.total_pages ELSE 0 END) * 8.0/1024 AS DECIMAL(18,3)) AS DataSizeMB,
            CAST(SUM(CASE WHEN i.type  > 1 THEN a.total_pages ELSE 0 END) * 8.0/1024 AS DECIMAL(18,3)) AS IndexSizeMB,
            CAST(SUM(a.total_pages)  * 8.0/1024 AS DECIMAL(18,3))                     AS TotalSizeMB,
            CAST(SUM(a.used_pages)   * 8.0/1024 AS DECIMAL(18,3))                     AS UsedSizeMB,
            CAST((SUM(a.total_pages)-SUM(a.used_pages)) * 8.0/1024 AS DECIMAL(18,3))  AS UnusedSizeMB,
            COUNT(DISTINCT i.index_id) - 1                                             AS IndexCount,
            MAX(CASE WHEN h.object_id IS NOT NULL THEN 'Heap' ELSE 'Clustered' END)   AS TableType
        FROM sys.tables t
        JOIN sys.indexes i           ON t.object_id = i.object_id
        JOIN sys.partitions p        ON i.object_id = p.object_id AND i.index_id = p.index_id
        JOIN sys.allocation_units a  ON p.partition_id = a.container_id
        LEFT JOIN (SELECT DISTINCT object_id FROM sys.indexes WHERE type = 0) h
                                     ON h.object_id = t.object_id
        OUTER APPLY (SELECT SUM(p2.rows) AS RowsCount
                     FROM sys.partitions p2
                     WHERE p2.object_id = t.object_id AND p2.index_id IN (0, 1)) rc
        WHERE t.is_ms_shipped = 0
        GROUP BY t.object_id, t.schema_id, t.name, rc.RowsCount
        ORDER BY SUM(a.total_pages) DESC
        """;
}
