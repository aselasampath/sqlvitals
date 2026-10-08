SELECT name                                           AS Name,
       CAST(compatibility_level AS INT)               AS CompatibilityLevel,
       CAST(is_auto_shrink_on AS INT)                 AS AutoShrink,
       ISNULL(page_verify_option_desc, N'')           AS PageVerify,
       CASE WHEN name = DB_NAME() THEN 1 ELSE 0 END   AS IsCurrent
FROM sys.databases
WHERE source_database_id IS NULL
