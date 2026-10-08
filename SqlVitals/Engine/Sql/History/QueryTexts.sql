SELECT x.QueryHash, DB_NAME(x.dbid) AS DatabaseName, x.QueryText
FROM (
    SELECT CONVERT(varchar(18), qs.query_hash, 1) AS QueryHash,
           st.dbid,
           LEFT(SUBSTRING(st.text, qs.statement_start_offset / 2 + 1,
                (CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                      ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2 + 1),
                @maxLength) AS QueryText,
           ROW_NUMBER() OVER (PARTITION BY qs.query_hash ORDER BY qs.last_execution_time DESC) AS rn
    FROM sys.dm_exec_query_stats qs
    CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
    WHERE CONVERT(varchar(18), qs.query_hash, 1) IN @hashes
) x
WHERE x.rn = 1 AND x.QueryText IS NOT NULL
