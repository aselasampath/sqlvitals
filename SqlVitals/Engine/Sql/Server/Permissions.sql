-- The login's effective permissions: the database's, and the server's except on Azure SQL
-- Database, which has no server scope.
IF CAST(SERVERPROPERTY('EngineEdition') AS INT) = 5
    SELECT permission_name AS PermissionName FROM sys.fn_my_permissions(NULL, N'DATABASE');
ELSE
    SELECT permission_name AS PermissionName FROM sys.fn_my_permissions(NULL, N'SERVER')
    UNION
    SELECT permission_name FROM sys.fn_my_permissions(NULL, N'DATABASE');
