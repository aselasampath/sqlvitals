-- What the login may read, for the permission check (#177): one row. HAS_PERMS_BY_NAME answers
-- for the login's effective permissions, so a role or a Windows group counts. Azure SQL Database
-- has no server scope and no msdb: only VIEW DATABASE STATE, in this database, is asked there.
DECLARE @edition INT = CAST(SERVERPROPERTY('EngineEdition') AS INT);

IF @edition = 5
    SELECT @edition                                                              AS Edition,
           SUSER_SNAME()                                                         AS LoginName,
           USER_NAME()                                                           AS UserName,
           DB_NAME()                                                             AS DatabaseName,
           CAST(1 AS INT)                                                        AS LoginIsOwn,
           ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE'), 0) AS ServerState,
           CAST(NULL AS INT) AS AnyDefinition,
           CAST(NULL AS INT) AS BackupSet,
           CAST(NULL AS INT) AS BackupMediaFamily,
           CAST(NULL AS INT) AS SysJobs,
           CAST(NULL AS INT) AS SysJobHistory,
           CAST(NULL AS INT) AS SysJobActivity,
           CAST(NULL AS INT) AS SysJobSchedules,
           CAST(NULL AS INT) AS SysSchedules,
           CAST(NULL AS INT) AS SysCategories,
           CAST(NULL AS INT) AS SysSessions;
ELSE
    SELECT @edition                                                              AS Edition,
           SUSER_SNAME()                                                         AS LoginName,
           USER_NAME()                                                           AS UserName,
           DB_NAME()                                                             AS DatabaseName,
           -- 0 when the login signs in through a Windows group: there is no login of its own to grant to.
           CASE WHEN EXISTS (SELECT 1 FROM sys.server_principals WHERE sid = SUSER_SID()) THEN 1 ELSE 0 END AS LoginIsOwn,
           ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE'), 0)        AS ServerState,
           ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DEFINITION'), 0)      AS AnyDefinition,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.backupset', 'OBJECT', 'SELECT'), 0)         AS BackupSet,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.backupmediafamily', 'OBJECT', 'SELECT'), 0) AS BackupMediaFamily,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobs', 'OBJECT', 'SELECT'), 0)           AS SysJobs,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobhistory', 'OBJECT', 'SELECT'), 0)     AS SysJobHistory,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobactivity', 'OBJECT', 'SELECT'), 0)    AS SysJobActivity,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysjobschedules', 'OBJECT', 'SELECT'), 0)   AS SysJobSchedules,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.sysschedules', 'OBJECT', 'SELECT'), 0)      AS SysSchedules,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.syscategories', 'OBJECT', 'SELECT'), 0)     AS SysCategories,
           ISNULL(HAS_PERMS_BY_NAME('msdb.dbo.syssessions', 'OBJECT', 'SELECT'), 0)       AS SysSessions;
