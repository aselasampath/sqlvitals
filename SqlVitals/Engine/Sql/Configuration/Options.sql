SELECT name AS Name, CAST(value AS BIGINT) AS Value, CAST(value_in_use AS BIGINT) AS ValueInUse
FROM sys.configurations
WHERE name IN (N'max degree of parallelism', N'cost threshold for parallelism', N'max server memory (MB)')
