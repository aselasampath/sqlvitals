SELECT TOP 20
    SCHEMA_NAME(t.schema_id)                                                   AS SchemaName,
    t.name                                                                     AS TableName,
    ISNULL(rc.RowsCount, 0)                                                    AS RowsCount,
    CAST(SUM(CASE WHEN i.type <= 1 THEN a.total_pages ELSE 0 END) * 8.0/1024 AS DECIMAL(18,3)) AS DataSizeMB,
    CAST(SUM(CASE WHEN i.type  > 1 THEN a.total_pages ELSE 0 END) * 8.0/1024 AS DECIMAL(18,3)) AS IndexSizeMB,
    CAST(SUM(a.total_pages)  * 8.0/1024 AS DECIMAL(18,3))                     AS TotalSizeMB,
    CAST(SUM(a.used_pages)   * 8.0/1024 AS DECIMAL(18,3))                     AS UsedSizeMB,
    CAST((SUM(a.total_pages)-SUM(a.used_pages)) * 8.0/1024 AS DECIMAL(18,3))  AS UnusedSizeMB,
    COUNT(DISTINCT i.index_id) - 1                                             AS IndexCount,
    MAX(CASE WHEN h.object_id IS NOT NULL THEN 'Heap' ELSE 'Clustered' END)   AS TableType
FROM sys.tables t
JOIN sys.indexes i           ON t.object_id = i.object_id
JOIN sys.partitions p        ON i.object_id = p.object_id AND i.index_id = p.index_id
JOIN sys.allocation_units a  ON p.partition_id = a.container_id
LEFT JOIN (SELECT DISTINCT object_id FROM sys.indexes WHERE type = 0) h
                             ON h.object_id = t.object_id
OUTER APPLY (SELECT SUM(p2.rows) AS RowsCount
             FROM sys.partitions p2
             WHERE p2.object_id = t.object_id AND p2.index_id IN (0, 1)) rc
WHERE t.is_ms_shipped = 0
GROUP BY t.object_id, t.schema_id, t.name, rc.RowsCount
ORDER BY SUM(a.total_pages) DESC
