SELECT RTRIM(counter_name) AS CounterName, cntr_value AS CntrValue, cntr_type AS CntrType
FROM sys.dm_os_performance_counters
/*COUNTER_FILTER*/
