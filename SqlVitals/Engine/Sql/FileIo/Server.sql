SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition,
       si.sqlserver_start_time AS ServerStartTime
FROM sys.dm_os_sys_info si
