SELECT
    pg.name                   AS GuideName,
    pg.scope_type_desc        AS GuideType,
    DB_NAME()                 AS DatabaseName,
    CAST(NULL AS nvarchar(256))            AS ObjectName,
    SUBSTRING(REPLACE(REPLACE(ISNULL(pg.query_text,''),CHAR(13),' '),CHAR(10),' '),1,400) AS QueryText,
    pg.is_disabled            AS IsDisabled
FROM sys.plan_guides pg
ORDER BY pg.is_disabled, pg.name;
