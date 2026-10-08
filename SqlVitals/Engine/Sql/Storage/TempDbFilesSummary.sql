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
