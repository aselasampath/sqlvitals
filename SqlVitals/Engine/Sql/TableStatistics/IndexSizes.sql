-- Filtered on the table, so this waits if the table is under a schema change: error 1222 means locked.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT index_id AS IndexId, SUM(row_count) AS RowsInIndex,
    SUM(reserved_page_count) AS ReservedPages, SUM(used_page_count) AS UsedPages
FROM sys.dm_db_partition_stats
WHERE object_id = @ObjectId
GROUP BY index_id
