using System.Buffers.Binary;
using SqlVitals.Engine.Security;

namespace SqlVitals.Engine.Tests.Security;

public class SecurityAuditTests
{
    private static SecuritySnapshot Server(
        SaLogin? sa = null, bool saMissing = false,
        IReadOnlyList<SysadminMember>? members = null,
        IReadOnlyList<DatabaseTrust>? databases = null,
        OrphanScan? orphans = null,
        bool canView = true, bool windowsOnly = false, int edition = 3) => new(
        edition,
        windowsOnly,
        canView,
        saMissing ? null : sa ?? new SaLogin("sa", IsDisabled: true),
        canView ? members ?? [] : null,
        databases ?? [new("master", false, "sa", true), new("Sales", false, "sa", true)],
        canView ? orphans ?? new OrphanScan(["master", "Sales"], [], [], []) : null);

    // The binary form of a Windows SID, as sys.server_principals.sid has it.
    private static byte[] Sid(string text)
    {
        var parts = text.Split('-');
        var subs  = parts.Skip(3).Select(uint.Parse).ToArray();
        var bytes = new byte[8 + subs.Length * 4];
        bytes[0] = 1;
        bytes[1] = (byte)subs.Length;
        bytes[7] = byte.Parse(parts[2]);
        for (var i = 0; i < subs.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + i * 4), subs[i]);
        return bytes;
    }

    private static SysadminMember Windows(string name, string sid, string type = "U", bool disabled = false) =>
        new(name, type, disabled, Sid: Sid(sid));

    private static SysadminMember SqlLogin(string name, bool disabled = false, bool policy = true, bool viaControlServer = false) =>
        new(name, "S", disabled, Sid: Guid.NewGuid().ToByteArray(), PasswordPolicyChecked: policy, ViaControlServer: viaControlServer);

    private static SecurityFinding Only(SecurityFindingList list, SecurityCheckKind kind) => Assert.Single(list.Findings, f => f.Kind == kind);

    private static SecurityFinding Member(SecurityFindingList list, string name) =>
        Assert.Single(list.Findings, f => f.Kind == SecurityCheckKind.SysadminMember && f.Subject == name);

    // ── SIDs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SidText_formats_a_binary_windows_sid()
    {
        Assert.Equal("S-1-5-32-544", SidText.Format([1, 2, 0, 0, 0, 0, 0, 5, 0x20, 0, 0, 0, 0x20, 2, 0, 0]));
        Assert.Equal("S-1-5-18",     SidText.Format([1, 1, 0, 0, 0, 0, 0, 5, 0x12, 0, 0, 0]));
        Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-513", SidText.Format(Sid("S-1-5-21-1004336348-1177238915-682003330-513")));
    }

    [Theory]
    [InlineData(new byte[] { 1 })]                                                   // sa: 0x01
    [InlineData(new byte[] { 0x5E, 0x1B, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 })] // a SQL login's GUID
    [InlineData(new byte[] { 1, 2, 0, 0, 0, 0, 0, 5, 0x20, 0, 0, 0 })]              // says 2 sub-authorities, has 1
    public void SidText_is_null_for_what_isnt_a_windows_sid(byte[] sid) => Assert.Null(SidText.Format(sid));

    // ── sa ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sa_enabled_with_sql_logins_allowed_is_a_risk_with_a_script()
    {
        var finding = Only(SecurityAudit.Run(Server(sa: new SaLogin("sa", false))), SecurityCheckKind.SaLogin);

        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        Assert.Equal("Server", finding.Scope);
        Assert.Equal("sa", finding.Subject);
        Assert.Equal("Enabled", finding.Finding);
        Assert.Contains("password-guessing", finding.Explanation);
        Assert.EndsWith("ALTER LOGIN [sa] DISABLE;", finding.FixScript);
    }

    [Fact]
    public void Sa_enabled_but_renamed_is_still_a_risk_and_says_so()
    {
        var finding = Only(SecurityAudit.Run(Server(sa: new SaLogin("dba_root", false))), SecurityCheckKind.SaLogin);

        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        Assert.Equal("Enabled (renamed to dba_root)", finding.Finding);
        Assert.Contains("renamed to dba_root", finding.Explanation);
        Assert.EndsWith("ALTER LOGIN [dba_root] DISABLE;", finding.FixScript);
    }

    [Fact]
    public void Sa_enabled_on_a_windows_only_server_is_to_review()
    {
        var finding = Only(SecurityAudit.Run(Server(sa: new SaLogin("sa", false), windowsOnly: true)), SecurityCheckKind.SaLogin);

        Assert.Equal(SecurityFindingStatus.Review, finding.Status);
        Assert.Contains("mixed mode", finding.Explanation);
        Assert.NotNull(finding.FixScript);
    }

    [Fact]
    public void Sa_disabled_passes()
    {
        var finding = Only(SecurityAudit.Run(Server(sa: new SaLogin("sa_off", true))), SecurityCheckKind.SaLogin);

        Assert.Equal(SecurityFindingStatus.Pass, finding.Status);
        Assert.Equal("Disabled (renamed to sa_off)", finding.Finding);
        Assert.Null(finding.FixScript);
    }

    [Fact]
    public void Sa_that_cant_be_seen_is_not_checked_and_names_the_permission()
    {
        var finding = Only(SecurityAudit.Run(Server(saMissing: true, canView: false)), SecurityCheckKind.SaLogin);

        Assert.Equal(SecurityFindingStatus.NotChecked, finding.Status);
        Assert.Contains("VIEW ANY DEFINITION", finding.Explanation);
    }

    // ── sysadmin ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"NT SERVICE\MSSQLSERVER")]
    [InlineData(@"NT SERVICE\SQLSERVERAGENT")]
    [InlineData(@"NT SERVICE\MSSQL$SQL2022")]
    [InlineData(@"NT SERVICE\SQLAgent$SQL2022")]
    [InlineData(@"NT SERVICE\SQLWriter")]
    [InlineData(@"NT SERVICE\Winmgmt")]
    public void Sql_servers_own_service_accounts_pass(string name)
    {
        var list = SecurityAudit.Run(Server(members: [Windows(name, "S-1-5-80-3880718306-3832830129-1677859214-2598158968-1052248003")]));

        var finding = Member(list, name);
        Assert.Equal(SecurityFindingStatus.Pass, finding.Status);
        Assert.Contains("setup makes sysadmin", finding.Explanation);
    }

    [Fact]
    public void Another_service_account_is_to_review()
    {
        var list = SecurityAudit.Run(Server(members: [Windows(@"NT SERVICE\SomeBackupAgent", "S-1-5-80-1-2-3-4-5")]));

        Assert.Equal(SecurityFindingStatus.Review, Member(list, @"NT SERVICE\SomeBackupAgent").Status);
    }

    [Theory]
    [InlineData(@"BUILTIN\Administrators",         "S-1-5-32-544", "local Administrators")]
    [InlineData(@"VORDEFINIERT\Administratoren",   "S-1-5-32-544", "local Administrators")]   // German Windows
    [InlineData(@"NT AUTHORITY\SYSTEM",            "S-1-5-18",     "LocalSystem")]
    [InlineData(@"NT AUTHORITY\Authenticated Users", "S-1-5-11",   "sign in to Windows")]
    [InlineData(@"CONTOSO\Domain Users",           "S-1-5-21-1004336348-1177238915-682003330-513", "Domain Users")]
    public void Broad_windows_principals_are_a_risk_whatever_the_language(string name, string sid, string expected)
    {
        var list = SecurityAudit.Run(Server(members: [Windows(name, sid, "G")]));

        var finding = Member(list, name);
        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        // What is wrong with this one comes first, where the grid's column shows it.
        Assert.Contains(expected, finding.Explanation.Split(". ")[0]);
        Assert.Contains(" is sysadmin", finding.Explanation);
        Assert.Contains("Members of sysadmin can do anything", finding.Explanation);
        Assert.EndsWith($"ALTER SERVER ROLE [sysadmin] DROP MEMBER [{name}];", finding.FixScript);
    }

    [Fact]
    public void A_disabled_broad_principal_is_to_review()
    {
        var list = SecurityAudit.Run(Server(members: [Windows(@"BUILTIN\Administrators", "S-1-5-32-544", "G", disabled: true)]));

        var finding = Member(list, @"BUILTIN\Administrators");
        Assert.Equal(SecurityFindingStatus.Review, finding.Status);
        Assert.Contains("disabled", finding.Explanation);
        Assert.Null(finding.FixScript);
    }

    [Fact]
    public void An_enabled_sql_login_is_a_risk_and_notes_check_policy_off()
    {
        var list = SecurityAudit.Run(Server(members: [SqlLogin("app_admin", policy: false)]));

        var finding = Member(list, "app_admin");
        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        Assert.Equal("SQL login · enabled", finding.Finding);
        Assert.Contains("password", finding.Explanation);
        Assert.Contains("CHECK_POLICY is OFF", finding.Explanation);
        Assert.Contains("DROP MEMBER [app_admin];", finding.FixScript);
    }

    [Fact]
    public void A_sql_login_with_check_policy_on_doesnt_mention_it()
    {
        var finding = Member(SecurityAudit.Run(Server(members: [SqlLogin("app_admin")])), "app_admin");

        Assert.DoesNotContain("CHECK_POLICY", finding.Explanation);
    }

    [Fact]
    public void A_sql_login_on_a_windows_only_server_or_disabled_is_to_review()
    {
        var windowsOnly = Member(SecurityAudit.Run(Server(members: [SqlLogin("app_admin")], windowsOnly: true)), "app_admin");
        var disabled    = Member(SecurityAudit.Run(Server(members: [SqlLogin("app_admin", disabled: true)])), "app_admin");

        Assert.Equal(SecurityFindingStatus.Review, windowsOnly.Status);
        Assert.Contains("Windows logins only", windowsOnly.Explanation);
        Assert.Equal(SecurityFindingStatus.Review, disabled.Status);
        Assert.Equal("SQL login · disabled", disabled.Finding);
    }

    [Fact]
    public void A_windows_group_or_person_is_to_review()
    {
        var list = SecurityAudit.Run(Server(members:
        [
            Windows(@"CONTOSO\SQL DBAs", "S-1-5-21-1004336348-1177238915-682003330-4101", "G"),
            Windows(@"CONTOSO\alice",    "S-1-5-21-1004336348-1177238915-682003330-1105"),
        ]));

        var group = Member(list, @"CONTOSO\SQL DBAs");
        Assert.Equal(SecurityFindingStatus.Review, group.Status);
        Assert.Equal("Windows group · enabled", group.Finding);
        Assert.Contains("Active Directory", group.Explanation);
        Assert.Equal(SecurityFindingStatus.Review, Member(list, @"CONTOSO\alice").Status);
    }

    [Fact]
    public void Control_server_is_listed_as_sysadmins_equal_with_a_revoke()
    {
        var list = SecurityAudit.Run(Server(members: [SqlLogin("monitoring", viaControlServer: true)]));

        var finding = Member(list, "monitoring");
        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        Assert.Equal("SQL login with CONTROL SERVER · enabled", finding.Finding);
        Assert.Contains("Treat it as sysadmin", finding.Explanation);
        Assert.EndsWith("REVOKE CONTROL SERVER FROM [monitoring] CASCADE;", finding.FixScript);
    }

    [Fact]
    public void A_group_with_control_server_says_what_its_members_have()
    {
        var group = Windows(@"CONTOSO\Helpdesk", "S-1-5-21-1-2-3-4102", "G") with { ViaControlServer = true };

        var finding = Member(SecurityAudit.Run(Server(members: [group])), @"CONTOSO\Helpdesk");

        Assert.Equal(SecurityFindingStatus.Review, finding.Status);
        Assert.StartsWith("Every member of this Windows group has CONTROL SERVER", finding.Explanation);
    }

    [Fact]
    public void A_sysadmin_member_that_also_holds_control_server_is_listed_once_as_a_member()
    {
        var list = SecurityAudit.Run(Server(members: [SqlLogin("app", viaControlServer: true), SqlLogin("app")]));

        var finding = Member(list, "app");
        Assert.Equal("SQL login · enabled", finding.Finding);
        Assert.Contains("DROP MEMBER", finding.FixScript);
    }

    [Fact]
    public void Sa_as_a_member_defers_to_the_sa_check()
    {
        var enabled  = Member(SecurityAudit.Run(Server(members: [new SysadminMember("sa", "S", false, IsSa: true)])), "sa");
        var disabled = Member(SecurityAudit.Run(Server(members: [new SysadminMember("sa", "S", true, IsSa: true)])), "sa");

        Assert.Equal(SecurityFindingStatus.Review, enabled.Status);
        Assert.Contains("see the sa login check", enabled.Explanation);
        Assert.Null(enabled.FixScript);
        Assert.Equal(SecurityFindingStatus.Pass, disabled.Status);
    }

    [Fact]
    public void Without_view_any_definition_members_and_orphans_are_not_checked()
    {
        var list = SecurityAudit.Run(Server(canView: false));

        Assert.Equal(SecurityFindingStatus.NotChecked, Only(list, SecurityCheckKind.SysadminMember).Status);
        Assert.Equal(SecurityFindingStatus.NotChecked, Only(list, SecurityCheckKind.OrphanedUser).Status);
        Assert.Contains("VIEW ANY DEFINITION", list.Problem);
        // TRUSTWORTHY is in sys.databases, which every login can read.
        Assert.All(list.Findings.Where(f => f.Kind == SecurityCheckKind.Trustworthy), f => Assert.Equal(SecurityFindingStatus.Pass, f.Status));
    }

    // ── TRUSTWORTHY ───────────────────────────────────────────────────────────

    [Fact]
    public void Trustworthy_with_a_sysadmin_owner_is_a_risk_of_becoming_sysadmin()
    {
        var list = SecurityAudit.Run(Server(databases: [new("Sales", true, "sa", true), new("master", false, "sa", true)]));

        var finding = Assert.Single(list.Findings, f => f.Kind == SecurityCheckKind.Trustworthy && f.IsRisk);
        Assert.Equal("Sales", finding.Scope);
        Assert.Equal("ON · owner sa", finding.Finding);
        Assert.Contains("make themselves sysadmin", finding.Explanation);
        Assert.EndsWith("ALTER DATABASE [Sales] SET TRUSTWORTHY OFF;", finding.FixScript);
    }

    [Fact]
    public void Trustworthy_with_an_owner_who_isnt_sysadmin_is_still_a_risk()
    {
        var list = SecurityAudit.Run(Server(databases: [new("Reports", true, @"CONTOSO\bob", false)]));

        var finding = Assert.Single(list.Findings, f => f.Kind == SecurityCheckKind.Trustworthy);
        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        Assert.Contains(@"isn't sysadmin", finding.Explanation);
        Assert.Contains("certificate", finding.Explanation);
    }

    [Fact]
    public void Trustworthy_with_an_owner_who_isnt_a_login_says_so()
    {
        var finding = Assert.Single(SecurityAudit.Run(Server(databases: [new("Old", true, null, null)])).Findings,
            f => f.Kind == SecurityCheckKind.Trustworthy);

        Assert.Equal("ON", finding.Finding);
        Assert.Contains("isn't a login on this server", finding.Explanation);
    }

    [Fact]
    public void Msdb_trustworthy_passes_as_it_ships_and_the_rest_are_grouped()
    {
        var list = SecurityAudit.Run(Server(databases:
        [
            new("master", false, "sa", true), new("msdb", true, "sa", true), new("Sales", false, "sa", true), new("HR", true, "sa", true),
        ]));
        var rows = list.Findings.Where(f => f.Kind == SecurityCheckKind.Trustworthy).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal("HR", rows[0].Scope);
        Assert.Equal(SecurityFindingStatus.Risk, rows[0].Status);
        var msdb = Assert.Single(rows, r => r.Scope == "msdb");
        Assert.Equal(SecurityFindingStatus.Pass, msdb.Status);
        Assert.Null(msdb.FixScript);
        Assert.Contains(rows, r => r.Scope == "2 other databases" && r.Finding == "OFF");
    }

    [Fact]
    public void Trustworthy_off_everywhere_is_one_pass_row()
    {
        var list = SecurityAudit.Run(Server());

        var finding = Only(list, SecurityCheckKind.Trustworthy);
        Assert.Equal(SecurityFindingStatus.Pass, finding.Status);
        Assert.Equal("All 2 databases", finding.Scope);
    }

    // ── Orphaned users ────────────────────────────────────────────────────────

    private static readonly byte[] UserSid = [0x5E, 0x1B, 0xC0, 0xDE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];

    [Fact]
    public void An_orphan_with_a_login_of_its_name_is_remapped_to_it()
    {
        var scan = new OrphanScan(["Sales", "HR"], [], [new OrphanedUser("Sales", "app", UserSid)], ["APP", "other"]);

        var finding = Assert.Single(SecurityAudit.Run(Server(orphans: scan)).Findings, f => f.Kind == SecurityCheckKind.OrphanedUser && f.IsRisk);
        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        Assert.Equal("Sales", finding.Scope);
        Assert.Equal("app", finding.Subject);
        Assert.Equal("SQL user · login APP exists", finding.Finding);
        Assert.Equal("USE [Sales];\nALTER USER [app] WITH LOGIN = [APP];", finding.FixScript);
    }

    [Fact]
    public void An_orphan_without_a_login_gets_both_choices_commented_out()
    {
        var user = new OrphanedUser("Sales", "legacy", UserSid, new DateTime(2021, 3, 4), OwnedSchemas: 2);
        var scan = new OrphanScan(["Sales"], [], [user], []);

        var finding = Assert.Single(SecurityAudit.Run(Server(orphans: scan)).Findings, f => f.Kind == SecurityCheckKind.OrphanedUser && f.IsRisk);
        Assert.Equal("SQL user, no login · created 2021-03-04", finding.Finding);
        Assert.Contains("once its 2 schemas have another owner", finding.Explanation);
        Assert.StartsWith("USE [Sales];", finding.FixScript);
        Assert.Contains("-- CREATE LOGIN [legacy] WITH PASSWORD = N'<a strong password>', SID = 0x5E1BC0DE000000000000000000000001, CHECK_POLICY = ON;", finding.FixScript);
        Assert.Contains("-- It owns 2 schemas", finding.FixScript);
        Assert.Contains("-- DROP USER [legacy];", finding.FixScript);
        // Nothing runs until one choice is picked.
        Assert.All(finding.FixScript!.Split('\n').Skip(1), line => Assert.StartsWith("--", line));
    }

    [Fact]
    public void Databases_that_couldnt_be_read_are_not_checked_and_the_clean_ones_pass_together()
    {
        var scan = new OrphanScan(["master", "Sales", "HR"], [new SkippedDatabase("Archive", "it is OFFLINE.")],
            [new OrphanedUser("Sales", "app", UserSid)], []);
        var rows = SecurityAudit.Run(Server(orphans: scan)).Findings.Where(f => f.Kind == SecurityCheckKind.OrphanedUser).ToList();

        Assert.Equal(3, rows.Count);
        var skipped = Assert.Single(rows, r => r.Status == SecurityFindingStatus.NotChecked);
        Assert.Equal("Archive", skipped.Scope);
        Assert.Contains("OFFLINE", skipped.Explanation);
        var pass = Assert.Single(rows, r => r.Status == SecurityFindingStatus.Pass);
        Assert.Equal("2 other databases", pass.Scope);
        Assert.Equal("None", pass.Finding);
    }

    [Fact]
    public void No_orphans_anywhere_is_one_pass_row()
    {
        var finding = Only(SecurityAudit.Run(Server()), SecurityCheckKind.OrphanedUser);

        Assert.Equal(SecurityFindingStatus.Pass, finding.Status);
        Assert.Equal("All 2 databases", finding.Scope);
    }

    [Fact]
    public void Names_are_quoted_in_every_script()
    {
        var scan = new OrphanScan(["we]ird"], [], [new OrphanedUser("we]ird", "o]dd", UserSid)], ["o]dd"]);
        var list = SecurityAudit.Run(Server(
            sa: new SaLogin("s]a", false),
            databases: [new("we]ird", true, "sa", true)],
            orphans: scan));

        Assert.EndsWith("ALTER LOGIN [s]]a] DISABLE;", Only(list, SecurityCheckKind.SaLogin).FixScript);
        Assert.EndsWith("ALTER DATABASE [we]]ird] SET TRUSTWORTHY OFF;", Only(list, SecurityCheckKind.Trustworthy).FixScript);
        Assert.Equal("USE [we]]ird];\nALTER USER [o]]dd] WITH LOGIN = [o]]dd];", Only(list, SecurityCheckKind.OrphanedUser).FixScript);
    }

    // ── The list ──────────────────────────────────────────────────────────────

    [Fact]
    public void Azure_sql_database_is_unavailable()
    {
        var list = SecurityAudit.Run(Server(edition: SecuritySnapshot.AzureSqlDatabaseEdition));

        Assert.False(list.Available);
        Assert.Empty(list.Findings);
        Assert.Contains("Managed Instance", list.Problem);
    }

    [Fact]
    public void Risks_come_first_then_review_not_checked_and_pass()
    {
        var scan = new OrphanScan(["master"], [new SkippedDatabase("Archive", "it is OFFLINE.")], [], []);
        var list = SecurityAudit.Run(Server(
            sa: new SaLogin("sa", false),
            members: [Windows(@"CONTOSO\alice", "S-1-5-21-1-2-3-1105"), SqlLogin("app")],
            orphans: scan));

        var statuses = list.Findings.Select(f => f.Status).ToList();
        Assert.Equal(statuses.OrderBy(s => s), statuses);
        Assert.Equal(SecurityCheckKind.SaLogin, list.Findings[0].Kind);   // risks in the order of the checks
        Assert.Equal(2, list.RiskCount);
        Assert.Equal(1, list.ReviewCount);
        Assert.Equal(1, list.NotCheckedCount);
        Assert.Null(list.Problem);
    }

    [Fact]
    public void The_fix_script_has_every_risk_and_nothing_else()
    {
        var list = SecurityAudit.Run(Server(
            sa: new SaLogin("sa", false),
            members: [SqlLogin("app"), Windows(@"CONTOSO\alice", "S-1-5-21-1-2-3-1105")],
            databases: [new("Sales", true, "sa", true)]));

        var script = list.FixScript("PROD01", new DateTime(2026, 9, 27, 10, 30, 0));

        Assert.NotNull(script);
        Assert.Contains("-- Security fixes for PROD01, generated by SqlVitals on 2026-09-27 10:30.", script);
        Assert.Contains("ALTER LOGIN [sa] DISABLE;", script);
        Assert.Contains("DROP MEMBER [app];", script);
        Assert.Contains("ALTER DATABASE [Sales] SET TRUSTWORTHY OFF;", script);
        Assert.DoesNotContain("alice", script);
        Assert.Equal(3, script!.Split("\nGO").Length - 1);
    }

    [Fact]
    public void The_fix_script_is_null_without_risks() =>
        Assert.Null(SecurityAudit.Run(Server()).FixScript("PROD01", DateTime.Now));
}
