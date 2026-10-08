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
