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
public class SecurityAuditRepository(SqlTarget target) : BaseRepository(target), ISecurityAuditRepository
{
    internal static readonly string ServerSql = SqlScripts.Get("SecurityAudit/Server");

    // By SID, so a renamed sa is still found.
    internal static readonly string SaSql = SqlScripts.Get("SecurityAudit/Sa");

    // Members of sysadmin, then whoever holds CONTROL SERVER, which is as good as sysadmin.
    internal static readonly string SysadminSql = SqlScripts.Get("SecurityAudit/Sysadmin");

    // Snapshots take their users and settings from their source database. IS_SRVROLEMEMBER is
    // given the owner only when it has a name: given NULL, it answers for the caller.
    internal static readonly string DatabasesSql = SqlScripts.Get("SecurityAudit/Databases");

    // SQL users made for a login (authentication_type 1) whose SID no login has. Users without a
    // login, contained users with a password, Windows users (who may get in through a group) and
    // dbo, guest, INFORMATION_SCHEMA and sys (principal_id 1 to 4) are left out. /*DATABASE*/ is the
    // database's quoted name.
    internal static readonly string OrphanedUsersSql = SqlScripts.Get("SecurityAudit/OrphanedUsers");

    internal static readonly string SqlLoginsSql = SqlScripts.Get("SecurityAudit/SqlLogins");

    // Who SqlVitals is connected as: its login, and the Windows groups and server roles it gets
    // permissions through. Any login can read its own token.
    internal static readonly string LoginTokenSql = SqlScripts.Get("SecurityAudit/LoginToken");

    /// <summary>The orphaned-user query for one database.</summary>
    internal static string OrphanedUsersIn(string database) =>
        OrphanedUsersSql.Replace("/*DATABASE*/", UnusedIndexDropScript.QuoteName(database));

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
