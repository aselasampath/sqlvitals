SELECT (SELECT COUNT(*) FROM sys.dm_os_schedulers WHERE status = N'VISIBLE ONLINE')     AS LogicalProcessors,
       (SELECT COUNT(*) FROM sys.dm_os_nodes
        WHERE node_state_desc NOT LIKE N'%DAC%' AND online_scheduler_count > 0)        AS NumaNodes,
       (SELECT MAX(online_scheduler_count) FROM sys.dm_os_nodes
        WHERE node_state_desc NOT LIKE N'%DAC%')                                       AS ProcessorsPerNumaNode,
       (SELECT physical_memory_kb / 1024 FROM sys.dm_os_sys_info)                      AS PhysicalMemoryMb
