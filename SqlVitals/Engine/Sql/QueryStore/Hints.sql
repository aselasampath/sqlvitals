SELECT h.query_id                            AS QueryId,
       h.query_hint_text                     AS HintText,
       h.source_desc                         AS Source,
       h.query_hint_failure_count            AS Failures,
       h.last_query_hint_failure_reason_desc AS LastFailureReason,
       qt.query_sql_text                     AS QueryText
FROM sys.query_store_query_hints h
LEFT JOIN sys.query_store_query q       ON q.query_id = h.query_id
LEFT JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
ORDER BY h.query_hint_failure_count DESC, h.query_id
