-- A table's statistics objects, from catalog views only. A columnstore index stays on the list, for
-- the page to say it has no histogram; only a hypothetical index isn't real.
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT st.stats_id AS StatsId, st.name AS Name,
    CAST(CASE WHEN i.index_id IS NULL THEN 0 ELSE 1 END AS bit) AS IsIndex,
    CAST(CASE WHEN i.type IN (1, 5) THEN 1 ELSE 0 END AS bit) AS IsClustered,
    CAST(CASE WHEN i.type IN (5, 6) THEN 1 ELSE 0 END AS bit) AS IsColumnstore,
    st.has_filter AS IsFiltered,
    c.name AS LeadingColumn
FROM sys.stats st
LEFT JOIN sys.indexes i ON i.object_id = st.object_id AND i.index_id = st.stats_id
LEFT JOIN sys.stats_columns sc ON sc.object_id = st.object_id AND sc.stats_id = st.stats_id AND sc.stats_column_id = 1
LEFT JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
WHERE st.object_id = @ObjectId AND (i.index_id IS NULL OR i.is_hypothetical = 0)
ORDER BY CASE WHEN i.type IN (1, 5) THEN 0 ELSE 1 END, st.name
