using System.Data;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Scripting;
using SqlVitals.Engine.Security;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads what the security audit (#43) looks at: the sa login, who is sysadmin or holds CONTROL
/// SERVER, each database's TRUSTWORTHY setting and owner, and each database's orphaned users.
/// Read only: catalog views, nothing written. Without VIEW ANY DEFINITION the logins and users
/// can't all be seen, so those checks are left out rather than guessed.
/// </summary>
public class SecurityAuditRepository(IConfiguration configuration) : BaseRepository(configuration), ISecurityAuditRepository
{
    internal const string ServerSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT)                                  AS Edition,
               CAST(ISNULL(SERVERPROPERTY('IsIntegratedSecurityOnly'), 0) AS INT)            AS WindowsAuthenticationOnly,
               CAST(ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DEFINITION'), 0) AS INT) AS CanViewDefinitions
        """;

    // By SID, so a renamed sa is still found.
    internal const string SaSql = """
        SELECT name AS Name, CAST(is_disabled AS INT) AS IsDisabled
        FROM sys.server_principals
        WHERE sid = 0x01
        """;

    // Members of sysadmin, then whoever holds CONTROL SERVER, which is as good as sysadmin.
    internal const string SysadminSql = """
        SELECT p.name                                          AS Name,
               p.type                                          AS Type,
               CAST(p.is_disabled AS INT)                      AS IsDisabled,
               p.create_date                                   AS CreateDate,
               p.sid                                           AS Sid,
               CASE WHEN p.sid = 0x01 THEN 1 ELSE 0 END        AS IsSa,
               CAST(sl.is_policy_checked AS INT)               AS PolicyChecked,
               0                                               AS ViaControlServer
        FROM sys.server_role_members rm
        JOIN sys.server_principals r ON r.principal_id = rm.role_principal_id
        JOIN sys.server_principals p ON p.principal_id = rm.member_principal_id
        LEFT JOIN sys.sql_logins sl  ON sl.principal_id = p.principal_id
        WHERE r.name = N'sysadmin'
        UNION ALL
        SELECT p.name, p.type, CAST(p.is_disabled AS INT), p.create_date, p.sid,
               CASE WHEN p.sid = 0x01 THEN 1 ELSE 0 END,
               CAST(sl.is_policy_checked AS INT),
               1
        FROM sys.server_permissions sp
        JOIN sys.server_principals p ON p.principal_id = sp.grantee_principal_id
        LEFT JOIN sys.sql_logins sl  ON sl.principal_id = p.principal_id
        WHERE sp.permission_name = N'CONTROL SERVER' AND sp.state IN ('G', 'W')
        """;

    // Snapshots take their users and settings from their source database. IS_SRVROLEMEMBER is
    // given the owner only when it has a name: given NULL, it answers for the caller.
    internal const string DatabasesSql = """
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
        """;

    // SQL users made for a login (authentication_type 1) whose SID no login has. Users without a
    // login, contained users with a password, Windows users (who may get in through a group) and
    // dbo, guest, INFORMATION_SCHEMA and sys (principal_id 1 to 4) are left out. {db} is the
    // database's quoted name.
    internal const string OrphanedUsersSql = """
        SELECT dp.name                                         AS Name,
               dp.sid                                          AS Sid,
               dp.create_date                                  AS CreateDate,
               (SELECT COUNT(*) FROM {db}.sys.schemas s WHERE s.principal_id = dp.principal_id) AS OwnedSchemas
        FROM {db}.sys.database_principals dp
        WHERE dp.type = 'S'
          AND dp.authentication_type = 1
          AND dp.principal_id > 4
          AND dp.sid IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid)
        """;

    internal const string SqlLoginsSql = """
        SELECT name AS Name
        FROM sys.server_principals
        WHERE type = 'S'
        """;

    // Who SqlVitals is connected as: its login, and the Windows groups and server roles it gets
    // permissions through. Any login can read its own token.
    internal const string LoginTokenSql = """
        SELECT sid AS Sid
        FROM sys.login_token
        WHERE usage = N'GRANT OR DENY' AND sid IS NOT NULL
        """;

    /// <summary>The orphaned-user query for one database.</summary>
    internal static string OrphanedUsersIn(string database) =>
        OrphanedUsersSql.Replace("{db}", UnusedIndexDropScript.QuoteName(database));

    public async Task<SecurityFindingList> GetSecurityAuditAsync()
    {
        using var conn = CreateConnection();

        var server      = await QFirst(conn, ServerSql);
        int edition     = server is null ? 0 : Convert.ToInt32(server.Edition);
        bool windowsOnly = server is not null && Convert.ToInt32(server.WindowsAuthenticationOnly) == 1;
        bool canView     = server is not null && Convert.ToInt32(server.CanViewDefinitions) == 1;

        if (edition == SecuritySnapshot.AzureSqlDatabaseEdition)
            return SecurityAudit.Run(new SecuritySnapshot(edition, windowsOnly, canView, null, null, [], null));

        var saRow = await QFirst(conn, SaSql);
        SaLogin? sa = saRow is null ? null : new SaLogin((string)saRow.Name, Convert.ToInt32(saRow.IsDisabled) == 1);

        var databases = (await Q(conn, DatabasesSql)).Select(r => new DatabaseRow(
            (string)r.Name,
            (string?)r.State ?? string.Empty,
            Convert.ToInt32(r.HasAccess) == 1,
            Convert.ToInt32(r.IsTrustworthy) == 1,
            (string?)r.Owner,
            r.OwnerIsSysadmin is null ? null : Convert.ToInt32(r.OwnerIsSysadmin) == 1)).ToList();

        var connectedSids = (await Q(conn, LoginTokenSql))
            .Select(r => Convert.ToHexString((byte[])r.Sid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<SysadminMember>? members = null;
        OrphanScan? orphans = null;
        if (canView)
        {
            members = (await Q(conn, SysadminSql)).Select(r => new SysadminMember(
                (string)r.Name,
                ((string)r.Type).Trim(),
                Convert.ToInt32(r.IsDisabled) == 1,
                (DateTime?)r.CreateDate,
                (byte[]?)r.Sid,
                Convert.ToInt32(r.IsSa) == 1,
                r.PolicyChecked is null ? null : Convert.ToInt32(r.PolicyChecked) == 1,
                Convert.ToInt32(r.ViaControlServer) == 1)).ToList();

            orphans = await ScanForOrphansAsync(conn, databases);
        }

        return SecurityAudit.Run(new SecuritySnapshot(
            edition, windowsOnly, canView, sa, members,
            databases.Select(d => new DatabaseTrust(d.Name, d.IsTrustworthy, d.Owner, d.OwnerIsSysadmin)).ToList(),
            orphans, connectedSids));
    }

    private sealed record DatabaseRow(string Name, string State, bool HasAccess, bool IsTrustworthy, string? Owner, bool? OwnerIsSysadmin);

    // One light query per database, so one that can't be read (offline, restoring, a secondary
    // that isn't readable) is reported on its own and the rest are still checked.
    private async Task<OrphanScan> ScanForOrphansAsync(IDbConnection conn, IReadOnlyList<DatabaseRow> databases)
    {
        var checkedDbs = new List<string>();
        var skipped    = new List<SkippedDatabase>();
        var users      = new List<OrphanedUser>();

        foreach (var db in databases)
        {
            if (!db.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new SkippedDatabase(db.Name, $"it is {db.State}."));
                continue;
            }
            if (!db.HasAccess)
            {
                skipped.Add(new SkippedDatabase(db.Name, "this login can't open it (it may be an availability group secondary that isn't readable)."));
                continue;
            }

            try
            {
                users.AddRange((await Q(conn, OrphanedUsersIn(db.Name))).Select(r => new OrphanedUser(
                    db.Name,
                    (string)r.Name,
                    (byte[])r.Sid,
                    (DateTime?)r.CreateDate,
                    Convert.ToInt32(r.OwnedSchemas))));
                checkedDbs.Add(db.Name);
            }
            catch (WaitStatsException ex)
            {
                skipped.Add(new SkippedDatabase(db.Name, ex.InnerException?.Message ?? ex.Message));
            }
        }

        IReadOnlyCollection<string> logins = users.Count == 0
            ? []
            : (await Q(conn, SqlLoginsSql)).Select(r => (string)r.Name).ToList();

        return new OrphanScan(checkedDbs, skipped, users, logins);
    }
}
