SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    CAST(0 AS BIGINT)                                                                         AS PhysicalMemoryKB,
    MAX(CASE WHEN RTRIM(pc.counter_name) = 'Total Server Memory (KB)'      THEN pc.cntr_value ELSE 0 END) AS TotalServerMemoryKB,
    MAX(CASE WHEN RTRIM(pc.counter_name) = 'Target Server Memory (KB)'     THEN pc.cntr_value ELSE 0 END) AS TargetServerMemoryKB,
    MAX(CASE WHEN RTRIM(pc.counter_name) = 'Database Cache Memory (KB)'    THEN pc.cntr_value ELSE 0 END) AS DatabaseCacheMemoryKB,
    MAX(CASE WHEN RTRIM(pc.counter_name) = 'SQL Cache Memory (KB)'         THEN pc.cntr_value ELSE 0 END) AS SqlCacheMemoryKB,
    MAX(CASE WHEN RTRIM(pc.counter_name) = 'Granted Workspace Memory (KB)' THEN pc.cntr_value ELSE 0 END) AS GrantedWorkspaceMemoryKB,
    MAX(CASE WHEN RTRIM(pc.counter_name) = 'Free Memory (KB)'              THEN pc.cntr_value ELSE 0 END) AS FreeMemoryKB,
    MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Pending'         THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsPending,
    MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Outstanding'     THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsOutstanding,
    CAST(0 AS BIGINT)                                                                         AS AvailablePhysicalKB,
    CAST(0 AS BIGINT)                                                                         AS TotalPageFileKB,
    CAST(0 AS BIGINT)                                                                         AS AvailablePageFileKB,
    N'N/A'                                                                                    AS SystemMemoryState,
    N'N/A'                                                                                    AS MemoryModel,
    GETDATE()                                                                                 AS CaptureTime
FROM sys.dm_os_performance_counters pc
WHERE RTRIM(pc.object_name) LIKE N'%Memory Manager%'
  AND RTRIM(pc.counter_name) IN (
    N'Total Server Memory (KB)', N'Target Server Memory (KB)',
    N'Database Cache Memory (KB)', N'SQL Cache Memory (KB)',
    N'Granted Workspace Memory (KB)',
    N'Free Memory (KB)', N'Memory Grants Pending', N'Memory Grants Outstanding'
  );
