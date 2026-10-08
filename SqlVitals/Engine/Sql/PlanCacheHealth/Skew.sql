SELECT TOP 20
    qp.session_id                      AS SessionId,
    qp.node_id                         AS NodeId,
    MAX(qp.row_count)                  AS MaxActualRows,
    MIN(qp.row_count)                  AS MinActualRows,
    CAST(MAX(qp.row_count) AS FLOAT)
        / NULLIF(MIN(qp.row_count), 0) AS SkewRatio,
    DB_NAME(r.database_id)             AS DatabaseName,
    SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,400) AS QueryText
FROM sys.dm_exec_query_profiles qp
JOIN sys.dm_exec_requests r ON r.session_id = qp.session_id AND r.request_id = qp.request_id
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
WHERE qp.session_id <> @@SPID
  AND qp.thread_id > 0
GROUP BY qp.session_id, qp.request_id, qp.node_id, r.database_id, st.text
HAVING COUNT(*) > 1
   AND MAX(qp.row_count) > 0
   AND CAST(MAX(qp.row_count) AS FLOAT)
       / NULLIF(MIN(qp.row_count), 0) >= 5
ORDER BY SkewRatio DESC;
