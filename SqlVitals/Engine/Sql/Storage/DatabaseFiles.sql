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
