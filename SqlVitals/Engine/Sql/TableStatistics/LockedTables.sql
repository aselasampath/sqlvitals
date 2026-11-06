-- Tables with a schema-modification lock held or waited for: a queued one blocks readers too.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT DISTINCT CAST(resource_associated_entity_id AS int) AS ObjectId
FROM sys.dm_tran_locks
WHERE resource_database_id = DB_ID() AND resource_type = N'OBJECT' AND request_mode = N'Sch-M'
