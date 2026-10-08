SELECT TOP 15 mc.type AS ClerkType, mc.name AS ClerkName,
    SUM(mc.pages_kb) AS TotalPagesKB,
    SUM(mc.pages_kb)/1024 AS TotalPagesMB,
    CAST(SUM(mc.pages_kb)*100.0/NULLIF((SELECT SUM(pages_kb) FROM sys.dm_os_memory_clerks),0) AS DECIMAL(10,2)) AS PctOfTotalMemory,
    GETDATE() AS CaptureTime
FROM sys.dm_os_memory_clerks mc
GROUP BY mc.type, mc.name ORDER BY TotalPagesKB DESC
