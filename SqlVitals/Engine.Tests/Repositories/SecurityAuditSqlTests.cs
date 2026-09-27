using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

public class SecurityAuditSqlTests
{
    [Fact]
    public void OrphanedUsers_quotes_the_database_name()
    {
        var sql = SecurityAuditRepository.OrphanedUsersIn("we]ird");

        Assert.Contains("FROM [we]]ird].sys.database_principals dp", sql);
        Assert.Contains("FROM [we]]ird].sys.schemas s", sql);
        Assert.DoesNotContain("{db}", sql);
    }

    [Fact]
    public void OrphanedUsers_are_sql_users_made_for_a_login_whose_sid_no_login_has()
    {
        var sql = SecurityAuditRepository.OrphanedUsersSql;

        // Not users without a login (0), contained users (2) or Windows users (3), who may get in through a group.
        Assert.Contains("dp.type = 'S'", sql);
        Assert.Contains("dp.authentication_type = 1", sql);
        // Not dbo, guest, INFORMATION_SCHEMA or sys.
        Assert.Contains("dp.principal_id > 4", sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid)", sql);
    }

    [Fact]
    public void Sa_is_found_by_its_sid_so_a_renamed_sa_is_too() =>
        Assert.Contains("WHERE sid = 0x01", SecurityAuditRepository.SaSql);

    [Fact]
    public void The_owner_is_only_checked_for_sysadmin_when_it_has_a_name()
    {
        // IS_SRVROLEMEMBER('sysadmin', NULL) answers for the caller, who is often sysadmin.
        var sql = SecurityAuditRepository.DatabasesSql;

        Assert.Contains("WHEN SUSER_SNAME(d.owner_sid) IS NULL THEN NULL", sql);
        Assert.Contains("IS_SRVROLEMEMBER(N'sysadmin', SUSER_SNAME(d.owner_sid))", sql);
        Assert.Contains("d.source_database_id IS NULL", sql);
    }

    [Fact]
    public void Sysadmin_includes_control_server_grants_but_not_denies()
    {
        var sql = SecurityAuditRepository.SysadminSql;

        Assert.Contains("r.name = N'sysadmin'", sql);
        Assert.Contains("sp.permission_name = N'CONTROL SERVER' AND sp.state IN ('G', 'W')", sql);
    }
}
