SELECT name AS Name, CAST(is_disabled AS INT) AS IsDisabled
FROM sys.server_principals
WHERE sid = 0x01
