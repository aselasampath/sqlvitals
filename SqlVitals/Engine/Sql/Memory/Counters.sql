SELECT counter_name AS CounterName, cntr_value AS CounterValue, GETDATE() AS CaptureTime
FROM sys.dm_os_performance_counters
WHERE object_name LIKE '%Memory Manager%'
  AND counter_name IN (
      'Memory Grants Outstanding','Memory Grants Pending',
      'Maximum Workspace Memory (KB)','Target Server Memory (KB)',
      'Total Server Memory (KB)','Stolen Server Memory (KB)',
      'Free Memory (KB)','Database Cache Memory (KB)')
ORDER BY counter_name
