SELECT p.name                                          AS Name,
       p.type                                          AS Type,
       CAST(p.is_disabled AS INT)                      AS IsDisabled,
       p.create_date                                   AS CreateDate,
       p.sid                                           AS Sid,
       CASE WHEN p.sid = 0x01 THEN 1 ELSE 0 END        AS IsSa,
       CAST(sl.is_policy_checked AS INT)               AS PolicyChecked,
       0                                               AS ViaControlServer
FROM sys.server_role_members rm
JOIN sys.server_principals r ON r.principal_id = rm.role_principal_id
JOIN sys.server_principals p ON p.principal_id = rm.member_principal_id
LEFT JOIN sys.sql_logins sl  ON sl.principal_id = p.principal_id
WHERE r.name = N'sysadmin'
UNION ALL
SELECT p.name, p.type, CAST(p.is_disabled AS INT), p.create_date, p.sid,
       CASE WHEN p.sid = 0x01 THEN 1 ELSE 0 END,
       CAST(sl.is_policy_checked AS INT),
       1
FROM sys.server_permissions sp
JOIN sys.server_principals p ON p.principal_id = sp.grantee_principal_id
LEFT JOIN sys.sql_logins sl  ON sl.principal_id = p.principal_id
WHERE sp.permission_name = N'CONTROL SERVER' AND sp.state IN ('G', 'W')
