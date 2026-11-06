-- Names from catalog views only, with no metadata function: under READ UNCOMMITTED these don't wait
-- on a schema change, and under READ COMMITTED they do (checked on 2017-2025, #242).
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT t.object_id AS ObjectId, s.name AS SchemaName, t.name AS TableName
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0
