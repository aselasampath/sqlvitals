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
