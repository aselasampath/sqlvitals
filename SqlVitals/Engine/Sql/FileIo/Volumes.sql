SELECT mf.database_id AS DatabaseId, mf.file_id AS FileId, vs.volume_mount_point AS Volume
FROM sys.master_files mf
JOIN sys.databases d ON d.database_id = mf.database_id AND d.state = 0
CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vs
