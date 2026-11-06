-- A scan of the view, unfiltered: it doesn't wait on a table under a schema change, while a filter on
-- that table's object_id does. That table's row can be mid-change, so the locked list replaces it.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT object_id AS ObjectId,
    SUM(CASE WHEN index_id IN (0, 1) THEN row_count ELSE 0 END) AS RowsInTable,
    SUM(CASE WHEN index_id IN (0, 1) THEN reserved_page_count ELSE 0 END) AS DataPages,
    SUM(CASE WHEN index_id > 1 THEN reserved_page_count ELSE 0 END) AS IndexPages
FROM sys.dm_db_partition_stats
GROUP BY object_id
