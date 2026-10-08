SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition,
       SYSDATETIME() AS ServerNow
