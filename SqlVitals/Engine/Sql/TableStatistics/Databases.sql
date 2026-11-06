SELECT d.name AS Name, DB_NAME() AS Connected
FROM sys.databases d
WHERE d.state = 0 AND HAS_DBACCESS(d.name) = 1
ORDER BY d.name
