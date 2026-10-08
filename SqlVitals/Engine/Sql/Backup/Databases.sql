WITH ranked AS (
    SELECT d.database_id,
           b.type,
           b.backup_start_date,
           b.backup_finish_date,
           CAST(ISNULL(b.compressed_backup_size, b.backup_size) AS BIGINT) AS bytes,
           CAST(b.is_copy_only AS INT) AS is_copy_only,
           b.media_set_id,
           ROW_NUMBER() OVER (PARTITION BY d.database_id, b.type
                              ORDER BY b.backup_finish_date DESC, b.backup_set_id DESC) AS rn
    FROM msdb.dbo.backupset b
    JOIN sys.databases d
      ON d.name = b.database_name COLLATE DATABASE_DEFAULT
     AND b.backup_finish_date >= d.create_date
    WHERE b.type IN ('D', 'I', 'L')
),
latest AS (
    SELECT r.database_id, r.type, r.backup_start_date, r.backup_finish_date, r.bytes, r.is_copy_only,
           (SELECT TOP (1) mf.physical_device_name
            FROM msdb.dbo.backupmediafamily mf
            WHERE mf.media_set_id = r.media_set_id
            ORDER BY mf.family_sequence_number) AS device
    FROM ranked r
    WHERE r.rn = 1
),
pivoted AS (
    SELECT l.database_id,
           MAX(CASE WHEN l.type = 'D' THEN l.backup_start_date END)  AS FullStart,
           MAX(CASE WHEN l.type = 'D' THEN l.backup_finish_date END) AS FullFinish,
           MAX(CASE WHEN l.type = 'D' THEN l.bytes END)              AS FullBytes,
           MAX(CASE WHEN l.type = 'D' THEN l.is_copy_only END)       AS FullCopyOnly,
           MAX(CASE WHEN l.type = 'D' THEN l.device END)             AS FullDevice,
           MAX(CASE WHEN l.type = 'I' THEN l.backup_start_date END)  AS DiffStart,
           MAX(CASE WHEN l.type = 'I' THEN l.backup_finish_date END) AS DiffFinish,
           MAX(CASE WHEN l.type = 'I' THEN l.bytes END)              AS DiffBytes,
           MAX(CASE WHEN l.type = 'I' THEN l.is_copy_only END)       AS DiffCopyOnly,
           MAX(CASE WHEN l.type = 'I' THEN l.device END)             AS DiffDevice,
           MAX(CASE WHEN l.type = 'L' THEN l.backup_start_date END)  AS LogStart,
           MAX(CASE WHEN l.type = 'L' THEN l.backup_finish_date END) AS LogFinish,
           MAX(CASE WHEN l.type = 'L' THEN l.bytes END)              AS LogBytes,
           MAX(CASE WHEN l.type = 'L' THEN l.is_copy_only END)       AS LogCopyOnly,
           MAX(CASE WHEN l.type = 'L' THEN l.device END)             AS LogDevice
    FROM latest l
    GROUP BY l.database_id
)
SELECT d.name                  AS Name,
       d.recovery_model_desc   AS RecoveryModel,
       d.state_desc            AS State,
       CAST(d.is_read_only AS INT) AS IsReadOnly,
       d.create_date           AS CreateDate,
       CASE WHEN d.replica_id IS NULL THEN 0 ELSE 1 END              AS InAvailabilityGroup,
       CASE WHEN drs.database_id IS NULL THEN 0 ELSE 1 END           AS LogChainKnown,
       CASE WHEN drs.last_log_backup_lsn IS NULL THEN 0 ELSE 1 END   AS LogChainStarted,
       p.FullStart, p.FullFinish, p.FullBytes, p.FullCopyOnly, p.FullDevice,
       p.DiffStart, p.DiffFinish, p.DiffBytes, p.DiffCopyOnly, p.DiffDevice,
       p.LogStart,  p.LogFinish,  p.LogBytes,  p.LogCopyOnly,  p.LogDevice
FROM sys.databases d
LEFT JOIN sys.database_recovery_status drs ON drs.database_id = d.database_id
LEFT JOIN pivoted p ON p.database_id = d.database_id
WHERE d.database_id <> 2
  AND d.source_database_id IS NULL
