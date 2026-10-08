SELECT CAST(value AS INT) AS Value
FROM sys.database_scoped_configurations
WHERE name = N'MAXDOP'
