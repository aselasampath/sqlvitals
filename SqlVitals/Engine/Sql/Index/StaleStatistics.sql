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
