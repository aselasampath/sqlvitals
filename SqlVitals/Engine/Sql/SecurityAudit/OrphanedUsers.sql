SELECT dp.name                                         AS Name,
       dp.sid                                          AS Sid,
       dp.create_date                                  AS CreateDate,
       (SELECT COUNT(*) FROM /*DATABASE*/.sys.schemas s WHERE s.principal_id = dp.principal_id) AS OwnedSchemas
FROM /*DATABASE*/.sys.database_principals dp
WHERE dp.type = 'S'
  AND dp.authentication_type = 1
  AND dp.principal_id > 4
  AND dp.sid IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid)
