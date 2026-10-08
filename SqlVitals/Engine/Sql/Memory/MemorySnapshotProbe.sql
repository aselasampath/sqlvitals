SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition,
       CASE WHEN EXISTS (SELECT 1 FROM sys.all_columns
                         WHERE object_id = OBJECT_ID(N'sys.dm_os_sys_info')
                           AND name = N'sql_memory_model_desc')
            THEN 1 ELSE 0 END                   AS HasMemoryModel
