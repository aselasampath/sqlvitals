-- The table to name in a GRANT, from catalog views, which don't wait; no row if the statistics are gone.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT s.name AS SchemaName, o.name AS TableName
FROM sys.stats st
JOIN sys.objects o ON o.object_id = st.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE st.object_id = @ObjectId AND st.stats_id = @StatsId
