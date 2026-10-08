SELECT TOP 20
    p.SessionId,
    p.NodeId,
    p.EstimatedRows,
    p.ActualRows,
    CAST(
        CASE
            WHEN p.ActualRows > p.EstimatedRows
                 THEN p.ActualRows * 1.0 / p.EstimatedRows
            ELSE p.EstimatedRows * 1.0 / p.ActualRows
        END AS FLOAT)  AS MisestimateRatio,
    DB_NAME(r.database_id) AS DatabaseName,
    SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,400) AS QueryText
FROM (
    SELECT
        qp.session_id               AS SessionId,
        qp.request_id               AS RequestId,
        qp.node_id                  AS NodeId,
        SUM(qp.estimate_row_count)  AS EstimatedRows,
        SUM(qp.row_count)           AS ActualRows
    FROM sys.dm_exec_query_profiles qp
    WHERE qp.session_id <> @@SPID
    GROUP BY qp.session_id, qp.request_id, qp.node_id
) p
JOIN sys.dm_exec_requests r ON r.session_id = p.SessionId AND r.request_id = p.RequestId
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
WHERE p.EstimatedRows > 0
  AND p.ActualRows > 0
  AND (   p.ActualRows * 1.0 / p.EstimatedRows >= 1000
       OR p.EstimatedRows * 1.0 / p.ActualRows >= 1000)
ORDER BY MisestimateRatio DESC;
