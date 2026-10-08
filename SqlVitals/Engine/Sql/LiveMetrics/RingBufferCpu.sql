(SELECT TOP 1
    x.xml_record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int')
 FROM (
    SELECT timestamp, CONVERT(XML, record) AS xml_record
    FROM sys.dm_os_ring_buffers
    WHERE ring_buffer_type = 'RING_BUFFER_SCHEDULER_MONITOR'
   AND record LIKE '%<SystemHealth>%'
 ) AS x
 ORDER BY timestamp DESC)
