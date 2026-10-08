using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Database storage and configuration: settings, files, TempDB files and the largest tables. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class StorageRepository(SqlTarget target) : BaseRepository(target), IStorageRepository
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
        var sqlEdition = SqlScripts.Get("Server/Edition");

        // ── A) Server / database-scoped config ────────────────────────
        // On-prem: sys.configurations (server-wide)
        // Azure SQL DB: sys.database_scoped_configurations (DB-level only)
        var sqlConfigOnPrem = SqlScripts.Get("Storage/ConfigOnPrem");

        var sqlConfigAzure = SqlScripts.Get("Storage/ConfigAzure");

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

    private static readonly string DatabaseFilesSqlTemplate = SqlScripts.Get("Storage/DatabaseFiles");

    // Azure SQL Database: sys.database_files for the current database only, which is the one
    // FILEPROPERTY reads.
    private static readonly string DatabaseFilesAzureSql = SqlScripts.Get("Storage/DatabaseFilesAzure");

    // ── C) TempDB files ───────────────────────────────────────────────
    // In tempdb's own context, for FILEPROPERTY. Azure SQL DB doesn't allow it, so it's skipped there.
    internal static readonly string TempDbFilesSummarySql = SqlScripts.Get("Storage/TempDbFilesSummary");

    // ── D) Top 20 tables by storage (same on both) ────────────────────
    // One row per table: sizes add up every partition of every index, and the row count is the
    // heap's or clustered index's, summed over its partitions.
    internal static readonly string TopTablesSql = SqlScripts.Get("Storage/TopTables");
}
