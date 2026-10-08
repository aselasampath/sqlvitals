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
