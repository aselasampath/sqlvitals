SELECT GETDATE() AS ServerTime, si.sqlserver_start_time AS ServerStartTime,
       CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition
FROM sys.dm_os_sys_info si
