using SqlVitals.Engine.Security;

namespace SqlVitals.Engine.Tests.Security;

/// <summary>
/// "Script the risks" never removes the access SqlVitals is connected with (#109): running the
/// whole script would otherwise lock the DBA out part-way through.
/// </summary>
public class SecurityAuditOwnAccessTests
{
    private static readonly byte[] Administrators = [1, 2, 0, 0, 0, 0, 0, 5, 0x20, 0, 0, 0, 0x20, 2, 0, 0]; // S-1-5-32-544

    private static SecuritySnapshot Server(IReadOnlyList<SysadminMember> members, SaLogin? sa = null, IEnumerable<byte[]>? connected = null) => new(
        3, WindowsAuthenticationOnly: false, CanViewDefinitions: true,
        sa ?? new SaLogin("sa", IsDisabled: true),
        members,
        [new DatabaseTrust("master", false, "sa", true)],
        new OrphanScan(["master"], [], [], []),
        connected?.Select(Convert.ToHexString).ToHashSet());

    private static SysadminMember SqlLogin(string name, byte[] sid) => new(name, "S", false, Sid: sid, PasswordPolicyChecked: true);

    private static SecurityFinding Member(SecurityFindingList list, string name) =>
        Assert.Single(list.Findings, f => f.Kind == SecurityCheckKind.SysadminMember && f.Subject == name);

    private static void AssertAllCommented(string? script) =>
        Assert.All(script!.Split('\n'), line => Assert.StartsWith("--", line));

    [Fact]
    public void The_login_SqlVitals_is_connected_as_keeps_sysadmin_in_the_script()
    {
        var me   = SqlLogin("dba", [1, 2, 3, 4]);
        var list = SecurityAudit.Run(Server([me], connected: [me.Sid!]));
        var finding = Member(list, "dba");

        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        Assert.Contains("SqlVitals is connected through it", finding.Explanation);
        AssertAllCommented(finding.FixScript);
        Assert.Contains("-- ALTER SERVER ROLE [sysadmin] DROP MEMBER [dba];", finding.FixScript);

        var script = list.FixScript("PROD01", DateTime.Now)!;
        Assert.DoesNotContain(script.Split('\n'), line => line.StartsWith("ALTER SERVER ROLE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_group_SqlVitals_gets_sysadmin_through_keeps_it()
    {
        var group = new SysadminMember(@"BUILTIN\Administrators", "G", false, Sid: Administrators);
        var finding = Member(SecurityAudit.Run(Server([group], connected: [[9, 9], Administrators])), @"BUILTIN\Administrators");

        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        AssertAllCommented(finding.FixScript);
    }

    [Fact]
    public void Other_logins_are_still_removed()
    {
        var me   = SqlLogin("dba", [1, 2, 3, 4]);
        var list = SecurityAudit.Run(Server([me, SqlLogin("app", [5, 6, 7, 8])], connected: [me.Sid!]));

        Assert.EndsWith("ALTER SERVER ROLE [sysadmin] DROP MEMBER [app];", Member(list, "app").FixScript);
        Assert.DoesNotContain("connected through", Member(list, "app").Explanation);
    }

    [Fact]
    public void Sa_is_not_disabled_while_SqlVitals_is_connected_as_sa()
    {
        var list = SecurityAudit.Run(Server([], sa: new SaLogin("sa", IsDisabled: false), connected: [[0x01]]));
        var finding = Assert.Single(list.Findings, f => f.Kind == SecurityCheckKind.SaLogin);

        Assert.Equal(SecurityFindingStatus.Risk, finding.Status);
        Assert.Contains("SqlVitals is connected through it", finding.Explanation);
        AssertAllCommented(finding.FixScript);
        Assert.Contains("-- ALTER LOGIN [sa] DISABLE;", finding.FixScript);
    }

    [Fact]
    public void Without_the_login_token_the_fixes_are_unchanged()
    {
        var list = SecurityAudit.Run(Server([SqlLogin("dba", [1, 2, 3, 4])], sa: new SaLogin("sa", false)));

        Assert.EndsWith("ALTER SERVER ROLE [sysadmin] DROP MEMBER [dba];", Member(list, "dba").FixScript);
        Assert.EndsWith("ALTER LOGIN [sa] DISABLE;", Assert.Single(list.Findings, f => f.Kind == SecurityCheckKind.SaLogin).FixScript);
    }

    [Fact]
    public void A_line_break_in_a_connected_logins_name_stays_commented_out()
    {
        var me = SqlLogin("x\nALTER LOGIN [evil] ENABLE;--", [1, 2, 3, 4]);
        var finding = Member(SecurityAudit.Run(Server([me], connected: [me.Sid!])), me.Name);

        AssertAllCommented(finding.FixScript);
    }
}
