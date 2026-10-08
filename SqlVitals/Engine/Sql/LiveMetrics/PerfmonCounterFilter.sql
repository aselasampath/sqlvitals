WHERE (
       object_name LIKE '%SQL Statistics%'
    OR object_name LIKE '%Buffer Manager%'
    OR object_name LIKE '%Memory Manager%'
    OR object_name LIKE '%Access Methods%'
    OR object_name LIKE '%General Statistics%'
    OR object_name LIKE '%Wait Statistics%'
    OR (object_name LIKE '%Locks%'     AND instance_name = '_Total')
    OR (object_name LIKE '%Databases%' AND instance_name IN ('_Total',''))
)
AND instance_name NOT IN (
    'internal','master','model','msdb','tempdb','mssqlsystemresource'
)
AND counter_name IN (
    -- General Throughput
    'Batch Requests/sec',
    'SQL Compilations/sec',
    'SQL Re-Compilations/sec',
    'Query optimizations/sec',
    'Network IO waits',
    -- Memory Pressure
    'Memory Grants Pending',
    'Granted Workspace Memory (KB)',
    'Target Server Memory (KB)',
    'Total Server Memory (KB)',
    'Stolen Server Memory (KB)',
    'Lock Memory (KB)',
    'SQL Cache Memory (KB)',
    'Lazy writes/sec',
    'Free list stalls/sec',
    'Reduced memory grants/sec',
    'Memory grant queue waits',
    'Thread-safe memory objects waits',
    'Page reads/sec',
    'Readahead pages/sec',
    -- CPU / Compilation
    'Active parallel threads',
    'Active requests',
    'Queued requests',
    'Wait for the worker',
    -- I/O Pressure
    'Page writes/sec',
    'Checkpoint pages/sec',
    'Page lookups/sec',
    'Background writer pages/sec',
    'Log Flushes/sec',
    'Log Bytes Flushed/sec',
    'Log Flush Write Time (ms)',
    'Page IO latch waits',
    'Log buffer waits',
    'Log write waits',
    'Full Scans/sec',
    'Index Searches/sec',
    'Page Splits/sec',
    'Forwarded Records/sec',
    -- TempDB Pressure
    'Version Store Size (KB)',
    'Free Space in tempdb (KB)',
    'Active Temp Tables',
    'Version Generation rate (KB/s)',
    'Version Cleanup rate (KB/s)',
    'Temp Tables Creation Rate',
    'Workfiles Created/sec',
    'Worktables Created/sec',
    -- Lock / Blocking
    'Lock Requests/sec',
    'Lock Wait Time (ms)',
    'Lock Waits/sec',
    'Number of Deadlocks/sec',
    'Table Lock Escalations/sec',
    'Blocked tasks',
    'Lock waits',
    'Non-Page latch waits',
    'Page latch waits',
    'Processes blocked',
    'Lock Timeouts/sec',
    -- Extras
    'Page life expectancy',
    'Buffer cache hit ratio',
    'Transactions/sec'
)
ORDER BY object_name, counter_name, instance_name
