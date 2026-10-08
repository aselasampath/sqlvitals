SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT)             AS Edition,
       CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)) AS ProductVersion
