SELECT CAST(value_in_use AS INT) AS AgentXps
FROM sys.configurations
WHERE name = N'Agent XPs'
