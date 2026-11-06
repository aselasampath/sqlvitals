-- One table's statistics headers. sys.dm_db_stats_properties waits on a schema change even under READ
-- UNCOMMITTED, so 1222 means locked, and the short wait keeps a database of locked tables from taking
-- minutes. An index type that never has a histogram (XML 3, spatial 4, columnstore 5 and 6) isn't rated.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 500;
SELECT st.stats_id AS StatsId, p.last_updated AS LastUpdated, p.rows AS RowsInStats, p.rows_sampled AS RowsSampled,
    p.steps AS Steps, p.modification_counter AS Modifications
FROM sys.stats st
LEFT JOIN sys.indexes i ON i.object_id = st.object_id AND i.index_id = st.stats_id
OUTER APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) p
WHERE st.object_id = @ObjectId AND (i.index_id IS NULL OR (i.is_hypothetical = 0 AND i.type NOT IN (3, 4, 5, 6)))
