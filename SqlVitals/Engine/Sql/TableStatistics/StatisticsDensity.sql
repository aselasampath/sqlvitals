-- The density vector is only in DBCC SHOW_STATISTICS. Its names come from catalog views, which don't
-- wait, and go in as values, so no name can break out of them; the DBCC itself waits on a schema change.
-- Statistics with no histogram (empty, never built, columnstore) give no rows, and no error.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
DECLARE @table nvarchar(600), @statistics sysname;
SELECT @table = QUOTENAME(s.name) + N'.' + QUOTENAME(o.name), @statistics = st.name
FROM sys.stats st
JOIN sys.objects o ON o.object_id = st.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE st.object_id = @ObjectId AND st.stats_id = @StatsId;
DBCC SHOW_STATISTICS (@table, @statistics) WITH DENSITY_VECTOR, NO_INFOMSGS
