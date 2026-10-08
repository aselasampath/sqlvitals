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
