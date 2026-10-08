SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT)                                  AS Edition,
       CAST(ISNULL(SERVERPROPERTY('IsIntegratedSecurityOnly'), 0) AS INT)            AS WindowsAuthenticationOnly,
       CAST(ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DEFINITION'), 0) AS INT) AS CanViewDefinitions
