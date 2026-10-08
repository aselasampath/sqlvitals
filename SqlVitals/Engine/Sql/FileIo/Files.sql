SELECT SYSDATETIME() AS ServerTime,
       vfs.database_id AS DatabaseId,
       ISNULL(DB_NAME(vfs.database_id), CONCAT('database ', vfs.database_id)) AS DatabaseName,
       vfs.file_id AS FileId,
       ISNULL(mf.name, CONCAT('file ', vfs.file_id)) AS FileName,
       ISNULL(mf.type_desc, '') AS FileType,
       ISNULL(mf.physical_name, '') AS PhysicalName,
       vfs.num_of_reads AS Reads, vfs.num_of_bytes_read AS BytesRead, vfs.io_stall_read_ms AS ReadStallMs,
       vfs.num_of_writes AS Writes, vfs.num_of_bytes_written AS BytesWritten, vfs.io_stall_write_ms AS WriteStallMs,
       vfs.size_on_disk_bytes AS SizeBytes
FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
LEFT JOIN sys.master_files mf ON mf.database_id = vfs.database_id AND mf.file_id = vfs.file_id
WHERE vfs.database_id < 32761
