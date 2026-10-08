EXEC tempdb.sys.sp_executesql N'
    SELECT f.name AS FileName, f.type_desc AS FileType,
        f.physical_name AS PhysicalPath,
        CAST(f.size AS BIGINT) * 8 / 1024 AS FileSizeMB,
        CAST(ISNULL(FILEPROPERTY(f.name, ''SpaceUsed''), 0) AS BIGINT) * 8 / 1024 AS SpaceUsedMB,
        CAST(f.size - ISNULL(FILEPROPERTY(f.name, ''SpaceUsed''), 0) AS BIGINT) * 8 / 1024 AS FreeSpaceMB,
        CAST(ISNULL(FILEPROPERTY(f.name, ''SpaceUsed''), 0) * 100.0 / NULLIF(f.size, 0) AS DECIMAL(10,2)) AS UsedPct,
        CASE f.is_percent_growth WHEN 1 THEN CAST(f.growth AS VARCHAR(10)) + ''%''
            ELSE CAST(CAST(f.growth AS BIGINT) * 8 / 1024 AS VARCHAR(20)) + '' MB'' END AS AutoGrowth,
        GETDATE() AS CaptureTime
    FROM sys.database_files f
    ORDER BY f.type_desc, f.file_id;';
