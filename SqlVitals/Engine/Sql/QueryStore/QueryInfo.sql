SELECT q.query_id AS QueryId, q.object_id AS ObjectId, /*OBJECT_NAME*/ AS ObjectName, qt.query_sql_text AS QueryText
FROM sys.query_store_query q
JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
WHERE q.query_id = @queryId
