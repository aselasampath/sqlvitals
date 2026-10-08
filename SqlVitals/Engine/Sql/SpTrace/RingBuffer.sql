SELECT CAST(t.target_data AS NVARCHAR(MAX)) AS TargetData
FROM /*SESSIONS*/ s
JOIN /*TARGETS*/ t ON t.event_session_address = s.address
WHERE s.name = @name AND t.target_name = 'ring_buffer'
