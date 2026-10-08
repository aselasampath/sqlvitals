SELECT RTRIM(counter_name) AS CounterName, cntr_value AS CounterValue, GETDATE() AS CaptureTime
FROM sys.dm_os_performance_counters
WHERE cntr_type = 65792
  AND (   (object_name LIKE N'%:Transactions%'
           AND counter_name IN (N'Version Store Size (KB)', N'Free Space in tempdb (KB)'))
       OR (object_name LIKE N'%:Databases%' AND instance_name = N'tempdb'
           AND counter_name = N'Active Transactions')
       OR (object_name LIKE N'%:General Statistics%'
           AND counter_name IN (N'Active Temp Tables', N'Temp Tables For Destruction')))
