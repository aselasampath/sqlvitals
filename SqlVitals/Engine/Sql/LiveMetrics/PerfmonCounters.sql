SELECT
    RTRIM(object_name)   AS ObjectName,
    RTRIM(counter_name)  AS CounterName,
    RTRIM(instance_name) AS InstanceName,
    cntr_value           AS CntrValue,
    cntr_type            AS CntrType,
    GETDATE()            AS CaptureTime
FROM sys.dm_os_performance_counters
