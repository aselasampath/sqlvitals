SELECT DB_NAME() AS DatabaseName,
       vfs.file_id AS FileId,
       ISNULL(df.name, CONCAT('file ', vfs.file_id)) AS FileName,
       ISNULL(df.type_desc, '') AS FileType,
       vfs.num_of_reads AS Reads, vfs.num_of_bytes_read AS BytesRead, vfs.io_stall_read_ms AS ReadStallMs,
       vfs.num_of_writes AS Writes, vfs.num_of_bytes_written AS BytesWritten, vfs.io_stall_write_ms AS WriteStallMs
FROM sys.dm_io_virtual_file_stats(DB_ID(), NULL) vfs
LEFT JOIN sys.database_files df ON df.file_id = vfs.file_id
