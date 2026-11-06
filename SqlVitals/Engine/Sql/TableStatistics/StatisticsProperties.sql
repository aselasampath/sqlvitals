-- Waits on a schema change even under READ UNCOMMITTED: 1222 means locked.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT p.last_updated AS LastUpdated, p.rows AS RowsInStats, p.rows_sampled AS RowsSampled,
    p.steps AS Steps, p.modification_counter AS Modifications
FROM sys.dm_db_stats_properties(@ObjectId, @StatsId) p
