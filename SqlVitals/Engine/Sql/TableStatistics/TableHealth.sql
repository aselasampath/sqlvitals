-- One table's statistics headers. sys.dm_db_stats_properties waits on a schema change even under READ
-- UNCOMMITTED, with or without the permission to read: 1222 means locked, and the short wait keeps a
-- database of locked tables from taking minutes. For statistics never built it returns a row of NULLs;
-- for a login without SELECT on their columns (and not the table's owner, db_owner, db_ddladmin or
-- sysadmin) no row and no error, so that's how to tell them apart (checked on 2017-2025).
-- HAS_PERMS_BY_NAME is no way to ask: on a table under a schema change it says 0, even to sysadmin.
-- A columnstore index is here with a row of NULLs, and never has a histogram, so it isn't rated.
-- XML and spatial indexes have no row in sys.stats.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 500;
SELECT p.last_updated AS LastUpdated, p.rows AS RowsInStats, p.rows_sampled AS RowsSampled,
    p.steps AS Steps, p.modification_counter AS Modifications,
    CAST(CASE WHEN p.object_id IS NULL THEN 0 ELSE 1 END AS bit) AS Readable
FROM sys.stats st
LEFT JOIN sys.indexes i ON i.object_id = st.object_id AND i.index_id = st.stats_id
OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) p
WHERE st.object_id = @ObjectId AND (i.index_id IS NULL OR (i.is_hypothetical = 0 AND i.type NOT IN (5, 6)))
