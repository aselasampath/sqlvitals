SELECT
    MAX(CASE WHEN RTRIM(counter_name) = 'Total Server Memory (KB)'      THEN cntr_value END) AS TotalServerMemoryKB,
    MAX(CASE WHEN RTRIM(counter_name) = 'Target Server Memory (KB)'     THEN cntr_value END) AS TargetServerMemoryKB,
    MAX(CASE WHEN RTRIM(counter_name) = 'Database Cache Memory (KB)'    THEN cntr_value END) AS DatabaseCacheMemoryKB,
    MAX(CASE WHEN RTRIM(counter_name) = 'SQL Cache Memory (KB)'         THEN cntr_value END) AS SqlCacheMemoryKB,
    MAX(CASE WHEN RTRIM(counter_name) = 'Granted Workspace Memory (KB)' THEN cntr_value END) AS GrantedWorkspaceMemoryKB,
    MAX(CASE WHEN RTRIM(counter_name) = 'Free Memory (KB)'              THEN cntr_value END) AS FreeMemoryKB,
    MAX(CASE WHEN RTRIM(counter_name) = 'Memory Grants Outstanding'     THEN cntr_value END) AS MemoryGrantsOutstanding
FROM sys.dm_os_performance_counters
WHERE object_name LIKE '%Memory Manager%'
