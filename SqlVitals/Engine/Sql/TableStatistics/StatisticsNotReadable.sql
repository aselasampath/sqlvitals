-- The statistics objects the login may not read. sys.dm_db_stats_properties and _histogram return no
-- rows for those, which looks just like statistics never built; the rule they follow is SELECT on the
-- statistics' columns, owning the table, db_owner, db_ddladmin or sysadmin.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 500;
SELECT st.stats_id AS StatsId, s.name AS SchemaName, t.name AS TableName
FROM sys.stats st
JOIN sys.objects t ON t.object_id = st.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.stats_columns sc ON sc.object_id = st.object_id AND sc.stats_id = st.stats_id
LEFT JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
WHERE st.object_id = @ObjectId AND COALESCE(IS_ROLEMEMBER(N'db_ddladmin'), 0) = 0
GROUP BY st.stats_id, s.name, t.name
HAVING SUM(CASE WHEN HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(t.name), N'OBJECT', N'SELECT', c.name, N'COLUMN') = 1 THEN 0 ELSE 1 END) > 0
