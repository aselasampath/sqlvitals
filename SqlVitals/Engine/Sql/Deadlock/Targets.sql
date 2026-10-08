SELECT t.target_name AS TargetName,
       CASE WHEN t.target_name = N'event_file' THEN CAST(t.target_data AS NVARCHAR(MAX)) END AS TargetData
FROM sys.dm_xe_sessions s
JOIN sys.dm_xe_session_targets t ON t.event_session_address = s.address
WHERE s.name = N'system_health'
