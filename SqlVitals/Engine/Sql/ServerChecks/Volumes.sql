SELECT vs.volume_mount_point                  AS Volume,
       mf.physical_name                       AS PhysicalName,
       CAST(vs.total_bytes AS BIGINT)         AS TotalBytes,
       CAST(vs.available_bytes AS BIGINT)     AS FreeBytes
FROM sys.master_files mf
JOIN sys.databases d ON d.database_id = mf.database_id AND d.state = 0
CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vs
