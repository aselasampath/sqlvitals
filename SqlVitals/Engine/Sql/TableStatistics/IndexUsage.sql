SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT index_id AS IndexId, user_seeks + user_scans + user_lookups AS Reads, user_updates AS Writes
FROM sys.dm_db_index_usage_stats
WHERE database_id = DB_ID() AND object_id = @ObjectId
