SELECT name                       AS ParameterName,
       CAST(value_in_use AS BIGINT) AS CurrentValue,
       CAST(minimum      AS BIGINT) AS MinValue,
       CAST(maximum      AS BIGINT) AS MaxValue,
       description                AS Description,
       CAST(is_dynamic   AS BIT)  AS IsDynamic,
       CAST(is_advanced  AS BIT)  AS IsAdvanced
FROM sys.configurations
WHERE name IN (
    'max degree of parallelism','cost threshold for parallelism',
    'max server memory (MB)','min server memory (MB)',
    'optimize for ad hoc workloads','max worker threads',
    'lightweight pooling','priority boost','fill factor (%)',
    'index create memory (KB)','min memory per query (KB)',
    'query wait (s)','remote query timeout (s)','locks',
    'open objects','user connections','backup compression default',
    'Database Mail XPs','xp_cmdshell'
)
ORDER BY name
