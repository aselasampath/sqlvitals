SELECT name                                      AS Name,
       physical_name                             AS PhysicalName,
       CAST(size AS BIGINT) * 8 / 1024           AS SizeMb,
       CAST(is_percent_growth AS INT)            AS IsPercentGrowth,
       CASE WHEN is_percent_growth = 1 THEN CAST(growth AS BIGINT)
            ELSE CAST(growth AS BIGINT) * 8 / 1024 END AS Growth
FROM tempdb.sys.database_files
WHERE type = 0
ORDER BY file_id
