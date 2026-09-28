using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Security;

/// <summary>
/// A quick audit of the security settings that most often widen a server's attack surface (#43):
/// who is sysadmin, whether the sa login is enabled, databases marked TRUSTWORTHY, and orphaned
/// users. Each finding is a Risk, something to Review, or a Pass, with the risk explained; a risk
/// comes with the T-SQL to remove it.
/// </summary>
public static class SecurityAudit
{
    private const string Permission =
        "Grant it VIEW ANY DEFINITION, which shows the definitions of logins, users and roles but no data, to check them.";

    public static SecurityFindingList Run(SecuritySnapshot snapshot)
    {
        if (snapshot.IsAzureSqlDatabase)
            return SecurityFindingList.Unavailable(
                "Azure SQL Database has no sysadmin role, no sa login and no TRUSTWORTHY setting, and its logins live in the " +
                "logical server's master database, so this audit is for SQL Server and Azure SQL Managed Instance.");

        var findings = new List<SecurityFinding> { SaLogin(snapshot) };
        findings.AddRange(SysadminMembers(snapshot));
        findings.AddRange(Trustworthy(snapshot.Databases));
        findings.AddRange(OrphanedUsers(snapshot));

        return new SecurityFindingList(SecurityFindingList.Sort(findings), Problem(snapshot));
    }

    private static string? Problem(SecuritySnapshot snapshot) =>
        snapshot.CanViewDefinitions
            ? null
            : "This login can't see the server's other logins or the users in each database, so the sysadmin members and " +
              "orphaned users can't be listed. " + Permission;

    // ── sa ────────────────────────────────────────────────────────────────────

    internal static SecurityFinding SaLogin(SecuritySnapshot snapshot)
    {
        const string what =
            "sa is SQL Server's built-in administrator: it is always sysadmin, can't be dropped, and its name is known to " +
            "everyone, so it is the first login password-guessing tries. Microsoft recommends disabling it, or at least renaming " +
            "it, and administering the server with named logins, so every change can be traced to a person.";

        if (snapshot.Sa is not { } sa)
            return new SecurityFinding(SecurityCheckKind.SaLogin, SecurityFinding.ServerScope, "sa", SecurityFindingStatus.NotChecked, "",
                snapshot.CanViewDefinitions
                    ? "No login with sa's SID (0x01) was found."
                    : "This login can't see the sa login. " + Permission);

        var renamed = sa.IsRenamed ? $" (renamed to {sa.Name})" : "";

        if (sa.IsDisabled)
            return new SecurityFinding(SecurityCheckKind.SaLogin, SecurityFinding.ServerScope, sa.Name, SecurityFindingStatus.Pass,
                "Disabled" + renamed, what + " It is disabled, so nobody can sign in as it.");

        var fix = "-- First check that nothing signs in as sa (an application, a linked server, a job step's connection),\n" +
                  "-- and that another sysadmin login works.\n" +
                  $"ALTER LOGIN {Quote(sa.Name)} DISABLE;";
        var own = snapshot.IsConnectedThrough(SaSid);
        if (own)
            fix = KeepOwnAccess(fix);

        if (snapshot.WindowsAuthenticationOnly)
            return new SecurityFinding(SecurityCheckKind.SaLogin, SecurityFinding.ServerScope, sa.Name, SecurityFindingStatus.Review,
                "Enabled" + renamed + ", Windows logins only",
                what + " The server accepts Windows logins only, so sa can't sign in now, but it can as soon as mixed mode " +
                "(SQL Server and Windows authentication) is turned on." + (own ? OwnAccessNote : ""), fix);

        return new SecurityFinding(SecurityCheckKind.SaLogin, SecurityFinding.ServerScope, sa.Name, SecurityFindingStatus.Risk,
            "Enabled" + renamed,
            what + (sa.IsRenamed
                ? $" It has been renamed to {sa.Name}, which hides it from guessing by name, but it is enabled and signs in with a password."
                : " It is enabled, and the server accepts SQL Server logins, so anyone who can reach the server can try its password.") +
            (own ? OwnAccessNote : ""),
            fix);
    }

    private static readonly byte[] SaSid = [0x01];

    // ── Your own access ───────────────────────────────────────────────────────
    // "Script the risks" puts every fix in one script. A fix that takes sysadmin from the login
    // SqlVitals is connected through (or disables it) would lock the DBA out part-way through,
    // before the rest has run, so it stays in the script commented out.

    internal const string OwnAccessNote =
        " SqlVitals is connected through it, so its fix is left commented out: it would remove your own access " +
        "part-way through the script. Run it signed in as another sysadmin.";

    internal static string KeepOwnAccess(string fix) =>
        "-- Left commented out: SqlVitals is connected through this login, so this would remove your own access.\n" +
        "-- Sign in as another sysadmin, check that login works, then run the statement below without its --.\n" +
        string.Join("\n", fix.Split('\n').Select(line =>
            line.StartsWith("--", StringComparison.Ordinal) ? ScriptText.OneLine(line) : ScriptText.Comment(line)));

    // ── sysadmin ──────────────────────────────────────────────────────────────

    private const string SysadminWhat =
        "Members of sysadmin can do anything on the server: read and change every database, create logins, turn auditing " +
        "off, and run operating system commands through xp_cmdshell or SQL Server Agent. Keep the role to the few people " +
        "and services that need it, and grant everyone else only the permissions they use.";

    private const string ControlServerWhat =
        "CONTROL SERVER gives almost everything sysadmin does: every database, every login and every server setting. " +
        "Treat it as sysadmin.";

    // Windows' well-known accounts, by SID so the check works whatever the server's language.
    private static readonly Dictionary<string, string> BroadWindowsPrincipals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S-1-5-32-544"] = "Every member of the server's local Administrators group {is}, including accounts added for " +
                           "other software and anyone who is made a local administrator. SQL Server setup stopped adding " +
                           "this group in SQL Server 2008; add a group of the DBAs instead.",
        ["S-1-5-18"]     = "Every service and scheduled task that runs as LocalSystem on the server {is}, so a flaw in any " +
                           "of them hands over SQL Server too.",
        ["S-1-5-11"]     = "Every account that can sign in to Windows {is}.",
        ["S-1-5-32-545"] = "Every user of the server's Windows {is}.",
        ["S-1-1-0"]      = "Everyone {is}, even guest accounts.",
    };

    // RIDs of domain groups that hold every user or computer of the domain.
    private static readonly Dictionary<string, string> BroadDomainGroups = new()
    {
        ["513"] = "Every user in the domain {is} (Domain Users).",
        ["515"] = "Every computer in the domain {is} (Domain Computers).",
    };

    // The service SIDs SQL Server setup adds to sysadmin, default and named instances.
    private static readonly string[] SetupServiceAccounts = ["MSSQLSERVER", "MSSQL$", "SQLSERVERAGENT", "SQLAGENT$", "SQLWRITER", "WINMGMT"];

    /// <summary>Why a Windows login or group this broad shouldn't be sysadmin, with {is} for "is sysadmin"; null when it isn't one of them.</summary>
    internal static string? BroadPrincipal(SysadminMember member)
    {
        if (member.WindowsSid is not { } sid)
            return null;
        if (BroadWindowsPrincipals.TryGetValue(sid, out var why))
            return why;
        if (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && BroadDomainGroups.TryGetValue(sid[(sid.LastIndexOf('-') + 1)..], out why))
            return why;
        return null;
    }

    /// <summary>NT SERVICE\MSSQLSERVER and the other service accounts SQL Server setup makes sysadmin.</summary>
    internal static bool IsSetupServiceAccount(SysadminMember member)
    {
        // Service SIDs are S-1-5-80-…, named NT SERVICE\<service>.
        if (member.Type != "U" || member.WindowsSid?.StartsWith("S-1-5-80-", StringComparison.Ordinal) == false)
            return false;
        var slash = member.Name.IndexOf('\\');
        if (slash < 0 || !member.Name[..slash].Equals("NT SERVICE", StringComparison.OrdinalIgnoreCase))
            return false;
        var service = member.Name[(slash + 1)..];
        return SetupServiceAccounts.Any(s => s.EndsWith('$')
            ? service.StartsWith(s, StringComparison.OrdinalIgnoreCase)
            : service.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    internal static IEnumerable<SecurityFinding> SysadminMembers(SecuritySnapshot snapshot)
    {
        if (snapshot.SysadminMembers is not { } members)
        {
            yield return new SecurityFinding(SecurityCheckKind.SysadminMember, SecurityFinding.ServerScope, "sysadmin",
                SecurityFindingStatus.NotChecked, "", "This login can't see who is in sysadmin. " + Permission);
            yield break;
        }

        // A member of sysadmin that also holds CONTROL SERVER is listed once, as a member.
        var listed = members
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(m => !m.ViaControlServer) ?? g.First())
            .OrderBy(m => m.ViaControlServer)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (var member in listed)
            yield return Member(member, snapshot.WindowsAuthenticationOnly, snapshot.IsConnectedThrough(member.Sid));
    }

    /// <param name="connectedThrough">SqlVitals is connected as this login, or through this group or role.</param>
    internal static SecurityFinding Member(SysadminMember member, bool windowsAuthenticationOnly, bool connectedThrough = false)
    {
        var what    = member.ViaControlServer ? ControlServerWhat : SysadminWhat;
        var holds   = member.ViaControlServer ? "CONTROL SERVER" : "sysadmin";
        var @is     = member.ViaControlServer ? "has CONTROL SERVER" : "is sysadmin";
        var finding = member.TypeText + (member.ViaControlServer ? " with CONTROL SERVER" : "") +
                      (member.IsDisabled ? " · disabled" : " · enabled");

        SecurityFinding Row(SecurityFindingStatus status, string why, string? fix = null)
        {
            var own = fix is not null && connectedThrough;
            return new(SecurityCheckKind.SysadminMember, SecurityFinding.ServerScope, member.Name, status, finding,
                why.Replace("{is}", @is) + " " + what + (own ? OwnAccessNote : ""), own ? KeepOwnAccess(fix!) : fix);
        }

        var disabled = member.IsDisabled
            ? " It is disabled, so it can't sign in now, but anyone who can alter logins can enable it again."
            : "";

        if (member.IsSa)
            return Row(member.IsDisabled ? SecurityFindingStatus.Pass : SecurityFindingStatus.Review,
                "This is the built-in sa login, which is always sysadmin; see the sa login check." + disabled);

        if (IsSetupServiceAccount(member))
            return Row(SecurityFindingStatus.Pass,
                "This is one of SQL Server's own service accounts, which setup makes sysadmin: the Database Engine, SQL Server " +
                "Agent, SQL Writer (for VSS backups) or WMI. Leave it.");

        var fix = member.ViaControlServer
            ? $"-- Grant it only the permissions it uses first, or whatever depends on it stops working.\nREVOKE CONTROL SERVER FROM {Quote(member.Name)} CASCADE;"
            : $"-- Grant it only the permissions it uses first, or whatever depends on it stops working.\nALTER SERVER ROLE [sysadmin] DROP MEMBER {Quote(member.Name)};";

        if (BroadPrincipal(member) is { } broad)
            return member.IsDisabled
                ? Row(SecurityFindingStatus.Review, broad + disabled)
                : Row(SecurityFindingStatus.Risk, broad, fix);

        if (member.IsSqlLogin)
        {
            var policy = member.PasswordPolicyChecked == false
                ? " CHECK_POLICY is OFF, so its password isn't held to Windows' length and complexity rules, and failed " +
                  "sign-ins never lock it out."
                : "";
            var why = $"A SQL login signs in with a password, which can be guessed, reused elsewhere or read from an " +
                      $"application's connection string, and with {holds} it holds the whole server. An application should " +
                      "get only the permissions it needs; people should use Windows logins." + policy;

            if (member.IsDisabled)
                return Row(SecurityFindingStatus.Review, why + disabled);
            if (windowsAuthenticationOnly)
                return Row(SecurityFindingStatus.Review, why + " The server accepts Windows logins only, so it can't sign in " +
                                                         "until mixed mode is turned on.");
            return Row(SecurityFindingStatus.Risk, why, fix);
        }

        var review = member.Type switch
        {
            "G" => "Every member of this Windows group {is}, and who is in the group is managed in Active Directory, outside " +
                   "SQL Server. Check who is in it.",
            "X" => "Every member of this Microsoft Entra group {is}, and who is in the group is managed in Microsoft Entra ID, " +
                   "outside SQL Server. Check who is in it.",
            "C" => $"Code signed with this certificate runs with {holds}. Check which modules are signed with the certificate.",
            "K" => $"Code signed with this asymmetric key runs with {holds}. Check which modules are signed with the key.",
            "R" => "Every member of this server role {is}. Check who is in the role.",
            _   => $"Check that this person or service account still needs {holds}.",
        };
        return Row(SecurityFindingStatus.Review, review + disabled);
    }

    // ── TRUSTWORTHY ───────────────────────────────────────────────────────────

    internal static IEnumerable<SecurityFinding> Trustworthy(IReadOnlyList<DatabaseTrust> databases)
    {
        const string what =
            "TRUSTWORTHY ON tells SQL Server to trust the database's code outside the database: a procedure in it that uses " +
            "EXECUTE AS can act on the server with the permissions of the database's owner.";

        // msdb ships TRUSTWORTHY ON, and SQL Server Agent, Database Mail and other features rely on it.
        static bool IsMsdb(DatabaseTrust d) => d.Name.Equals("msdb", StringComparison.OrdinalIgnoreCase);

        var trusted = databases.Where(d => d.IsTrustworthy && !IsMsdb(d)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var db in trusted)
        {
            var owner = db.Owner is null
                ? " Its owner isn't a login on this server, so what that code can reach can't be told."
                : db.OwnerIsSysadmin == true
                    ? $" Its owner, {db.Owner}, is sysadmin, so anyone who is db_owner in this database, or can create a procedure " +
                      "in it, can make themselves sysadmin."
                    : db.OwnerIsSysadmin == false
                        ? $" Its owner, {db.Owner}, isn't sysadmin, so that code gets {db.Owner}'s permissions; still, few " +
                          "databases need the setting: sign the code that does with a certificate instead."
                        : $" Whether its owner, {db.Owner}, is sysadmin couldn't be read; if it is, anyone who is db_owner in " +
                          "this database can make themselves sysadmin.";
            yield return new SecurityFinding(SecurityCheckKind.Trustworthy, db.Name, db.Name, SecurityFindingStatus.Risk,
                "ON" + (db.Owner is null ? "" : $" · owner {db.Owner}"), what + owner,
                "-- First check that nothing in it needs the setting: CLR assemblies marked EXTERNAL_ACCESS or UNSAFE, and code\n" +
                "-- that uses EXECUTE AS to reach other databases or the server. Sign that code with a certificate instead.\n" +
                $"ALTER DATABASE {Quote(db.Name)} SET TRUSTWORTHY OFF;");
        }

        if (databases.FirstOrDefault(d => d.IsTrustworthy && IsMsdb(d)) is { } msdb)
            yield return new SecurityFinding(SecurityCheckKind.Trustworthy, msdb.Name, msdb.Name, SecurityFindingStatus.Pass,
                "ON, as it ships",
                what + " msdb is TRUSTWORTHY ON as installed, and SQL Server Agent, Database Mail and other features rely on it. " +
                "Leave it, and keep db_owner in msdb to sysadmins.");

        var off = databases.Where(d => !d.IsTrustworthy).Select(d => d.Name).ToList();
        if (off.Count > 0)
            yield return new SecurityFinding(SecurityCheckKind.Trustworthy, Databases(off, databases.Count - off.Count),
                off.Count == 1 ? off[0] : "", SecurityFindingStatus.Pass, "OFF",
                what + " With it OFF, the database's code stays inside the database.");
    }

    // ── Orphaned users ────────────────────────────────────────────────────────

    internal static IEnumerable<SecurityFinding> OrphanedUsers(SecuritySnapshot snapshot)
    {
        const string what =
            "An orphaned user is a SQL user whose SID matches no login on this server, usually because the database was " +
            "restored or attached from another server, or its login was dropped. Whatever signs in as it can't reach this " +
            "database, and its permissions stay in place for whoever next creates a login with its SID, or has it remapped " +
            "with ALTER USER WITH LOGIN.";

        if (snapshot.Orphans is not { } scan)
        {
            yield return new SecurityFinding(SecurityCheckKind.OrphanedUser, SecurityFinding.ServerScope, "", SecurityFindingStatus.NotChecked, "",
                "Without seeing every login and every database's users, every user would look orphaned. " + Permission);
            yield break;
        }

        var logins = scan.SqlLogins.ToLookup(l => l, StringComparer.OrdinalIgnoreCase);
        var users  = scan.Users
            .OrderBy(u => u.Database, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var user in users)
        {
            var login = logins[user.Name].FirstOrDefault();
            var created = user.CreateDate is { } date ? $" · created {date:yyyy-MM-dd}" : "";
            yield return new SecurityFinding(SecurityCheckKind.OrphanedUser, user.Database, user.Name, SecurityFindingStatus.Risk,
                login is null ? "SQL user, no login" + created : $"SQL user · login {login} exists" + created,
                what + (login is null
                    ? " No login of its name exists here. If the database came from another server, create its login here with " +
                      "the user's SID, so the two match; if nobody needs the user any more, drop it" +
                      (user.OwnedSchemas > 0 ? $", once its {Plural(user.OwnedSchemas, "schema")} {(user.OwnedSchemas == 1 ? "has" : "have")} another owner." : ".")
                    : $" A SQL login called {login} exists here but has a different SID: remapping the user to it gives that " +
                      "login the user's access."),
                OrphanFix(user, login));
        }

        foreach (var skipped in scan.Skipped.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            yield return new SecurityFinding(SecurityCheckKind.OrphanedUser, skipped.Name, "", SecurityFindingStatus.NotChecked, "Not read",
                $"This database's users couldn't be read: {skipped.Reason}");

        var withOrphans = scan.Users.Select(u => u.Database).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var clean = scan.CheckedDatabases.Where(d => !scan.Users.Any(u => u.Database.Equals(d, StringComparison.OrdinalIgnoreCase))).ToList();
        if (clean.Count > 0)
            yield return new SecurityFinding(SecurityCheckKind.OrphanedUser, Databases(clean, withOrphans + scan.Skipped.Count),
                "", SecurityFindingStatus.Pass, "None", what + " Every SQL user in " +
                (clean.Count == 1 ? clean[0] : $"these {clean.Count:N0} databases") + " matches a login.");
    }

    internal static string OrphanFix(OrphanedUser user, string? login)
    {
        var use = $"USE {Quote(user.Database)};\n";
        if (login is not null)
            return use + $"ALTER USER {Quote(user.Name)} WITH LOGIN = {Quote(login)};";

        var schemas = user.OwnedSchemas > 0
            ? $"-- It owns {Plural(user.OwnedSchemas, "schema")}: give {(user.OwnedSchemas == 1 ? "it" : "them")} another owner first " +
              "(ALTER AUTHORIZATION ON SCHEMA::<schema> TO dbo).\n"
            : "";
        return use +
               "-- Choose one, and remove the -- from it.\n" +
               "-- Either create its login with the user's SID, so the two match (sp_help_revlogin on the old server copies the password too):\n" +
               ScriptText.Comment($"CREATE LOGIN {Quote(user.Name)} WITH PASSWORD = N'<a strong password>', SID = {user.SidHex}, CHECK_POLICY = ON;") + "\n" +
               "-- or, if nobody needs it any more, drop the user.\n" +
               schemas +
               ScriptText.Comment($"DROP USER {Quote(user.Name)};");
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    private static string Quote(string name) => UnusedIndexDropScript.QuoteName(name);

    // The scope of a Pass row for several databases: the one database's name, "All 12 databases" or "9 other databases".
    private static string Databases(IReadOnlyList<string> names, int others) =>
        names.Count == 1 ? names[0]
        : others == 0 ? $"All {names.Count:N0} databases"
        : $"{names.Count:N0} other databases";

    private static string Plural(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";
}
