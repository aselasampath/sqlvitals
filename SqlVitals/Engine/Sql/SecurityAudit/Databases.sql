SELECT d.name                                          AS Name,
       d.state_desc                                    AS State,
       CAST(ISNULL(HAS_DBACCESS(d.name), 0) AS INT)    AS HasAccess,
       CAST(d.is_trustworthy_on AS INT)                AS IsTrustworthy,
       SUSER_SNAME(d.owner_sid)                        AS Owner,
       CASE WHEN d.owner_sid = 0x01 THEN 1
            WHEN SUSER_SNAME(d.owner_sid) IS NULL THEN NULL
            ELSE IS_SRVROLEMEMBER(N'sysadmin', SUSER_SNAME(d.owner_sid)) END AS OwnerIsSysadmin
FROM sys.databases d
WHERE d.source_database_id IS NULL
ORDER BY d.name
