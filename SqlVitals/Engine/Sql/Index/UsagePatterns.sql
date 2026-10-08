SELECT OBJECT_NAME(i.object_id) AS TableName,
    i.name AS IndexName, i.type_desc AS IndexType,
    ISNULL(s.user_seeks,0)   AS UserSeeks,
    ISNULL(s.user_scans,0)   AS UserScans,
    ISNULL(s.user_lookups,0) AS UserLookups,
    ISNULL(s.user_updates,0) AS UserUpdates,
    s.last_user_seek   AS LastUserSeek,
    s.last_user_scan   AS LastUserScan,
    s.last_user_lookup AS LastUserLookup,
    s.last_user_update AS LastUserUpdate,
    CASE
        WHEN ISNULL(s.user_seeks,0)+ISNULL(s.user_scans,0)+ISNULL(s.user_lookups,0) = 0 THEN 'Unused'
        WHEN ISNULL(s.user_scans,0) > ISNULL(s.user_seeks,0) THEN 'Mostly Scanned - Review'
        ELSE 'Healthy'
    END AS IndexHealth,
    DB_NAME(DB_ID()) AS DatabaseName, GETDATE() AS CaptureTime
FROM sys.indexes i
JOIN sys.objects o ON i.object_id = o.object_id
LEFT JOIN sys.dm_db_index_usage_stats s
    ON i.object_id = s.object_id AND i.index_id = s.index_id AND s.database_id = DB_ID()
WHERE o.type = 'U' AND i.type > 0
ORDER BY ISNULL(s.user_seeks,0)+ISNULL(s.user_scans,0)+ISNULL(s.user_lookups,0) DESC
