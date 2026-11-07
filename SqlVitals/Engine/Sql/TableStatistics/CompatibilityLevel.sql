-- Picks the auto-update rule. sys.databases is a catalog view with no table to wait on.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT CAST(compatibility_level AS int) AS CompatibilityLevel
FROM sys.databases
WHERE database_id = DB_ID()
