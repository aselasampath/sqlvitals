SELECT TOP (@maxRows)
       b.type                                     AS Type,
       b.backup_start_date                        AS StartDate,
       b.backup_finish_date                       AS FinishDate,
       CAST(b.backup_size AS BIGINT)              AS Bytes,
       CAST(b.compressed_backup_size AS BIGINT)   AS CompressedBytes,
       CAST(b.is_copy_only AS INT)                AS CopyOnly,
       CAST(b.has_backup_checksums AS INT)        AS HasChecksums,
       CAST(b.is_damaged AS INT)                  AS IsDamaged,
       b.recovery_model                           AS RecoveryModel,
       b.user_name                                AS UserName,
       (SELECT TOP (1) mf.physical_device_name
        FROM msdb.dbo.backupmediafamily mf
        WHERE mf.media_set_id = b.media_set_id
        ORDER BY mf.family_sequence_number)       AS Device
FROM msdb.dbo.backupset b
WHERE b.database_name = @databaseName
  AND b.backup_finish_date IS NOT NULL
ORDER BY b.backup_finish_date DESC, b.backup_set_id DESC
