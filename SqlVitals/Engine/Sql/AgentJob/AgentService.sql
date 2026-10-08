SELECT TOP (1) status_desc AS Status
FROM sys.dm_server_services
WHERE servicename LIKE N'SQL Server Agent%'
