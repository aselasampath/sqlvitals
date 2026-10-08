SELECT name                                              AS ParameterName,
       CASE
           WHEN ISNUMERIC(CAST(value AS NVARCHAR(256))) = 1
           THEN CAST(CAST(value AS NVARCHAR(256)) AS BIGINT)
           ELSE 0
       END                                              AS CurrentValue,
       CAST(0  AS BIGINT)                               AS MinValue,
       CAST(0  AS BIGINT)                               AS MaxValue,
       CAST(ISNULL(CAST(value AS NVARCHAR(256)),'')
            AS NVARCHAR(MAX))                           AS Description,
       CAST(1  AS BIT)                                  AS IsDynamic,
       CAST(0  AS BIT)                                  AS IsAdvanced
FROM sys.database_scoped_configurations
ORDER BY name
