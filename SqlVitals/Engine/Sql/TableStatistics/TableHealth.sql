-- One table's statistics headers. sys.dm_db_stats_properties waits on a schema change even under READ
-- UNCOMMITTED, with or without the permission to read: 1222 means locked, and the short wait keeps a
-- database of locked tables from taking minutes. For statistics never built it returns a row of NULLs;
-- for a login without SELECT on their columns (and not the table's owner, db_owner, db_ddladmin or
-- sysadmin) no row and no error, so that's how to tell them apart (checked on 2017-2025).
-- HAS_PERMS_BY_NAME is no way to ask: on a table under a schema change it says 0, even to sysadmin.
-- A columnstore index is here with a row of NULLs, and never has a histogram, so it isn't rated.
-- XML and spatial indexes have no row in sys.stats. TableRows is the table's rows now, to tell statistics
-- never built on an empty table from those never built on one with rows.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 500;
SELECT p.last_updated AS LastUpdated, p.rows AS RowsInStats, p.rows_sampled AS RowsSampled,
    p.steps AS Steps, p.modification_counter AS Modifications,
    CAST(CASE WHEN p.object_id IS NULL THEN 0 ELSE 1 END AS bit) AS Readable,
    (SELECT SUM(pa.rows) FROM sys.partitions pa WHERE pa.object_id = st.object_id AND pa.index_id IN (0, 1)) AS TableRows
FROM sys.stats st
LEFT JOIN sys.indexes i ON i.object_id = st.object_id AND i.index_id = st.stats_id
OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) p
WHERE st.object_id = @ObjectId AND (i.index_id IS NULL OR (i.is_hypothetical = 0 AND i.type NOT IN (5, 6)))
