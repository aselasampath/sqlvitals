SELECT DB_NAME() AS DatabaseName, actual_state_desc AS ActualState
FROM sys.database_query_store_options
