using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVitals.Engine.Permissions;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Permissions;

/// <summary>The first-run permission check (#177): what the server answered → what's missing → the GRANT.</summary>
public sealed class PermissionCheckTests
{
    private const int SqlServer = 3;   // Enterprise
    private const int Express   = ServerCapabilities.ExpressEdition;
    private const int Mi        = ServerCapabilities.ManagedInstanceEdition;
    private const int AzureDb   = ServerCapabilities.AzureSqlDatabaseEdition;

    private static readonly string[] AllTables = [.. PermissionCheck.BackupTables, .. PermissionCheck.JobTables];

    private static PermissionProbe Probe(int edition = SqlServer, bool serverState = true, bool anyDefinition = true,
                                         IEnumerable<string>? readable = null, string login = "CONTOSO\\svc_vitals",
                                         bool loginIsOwn = true) =>
        new(edition, login, DatabaseUser: "vitals", Database: "Sales", loginIsOwn, serverState, anyDefinition,
            (readable ?? AllTables).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static PermissionResult Area(PermissionReport report, PermissionArea area) => report.Results.Single(r => r.Area == area);

    // ── Results ──────────────────────────────────────────────────────────

    [Fact]
    public void ASysadmin_HasEverything_AndNeedsNoScript()
    {
        var report = PermissionCheck.Evaluate(Probe());

        Assert.True(report.AllGranted);
        Assert.All(report.Results, r => Assert.Equal(PermissionState.Granted, r.State));
        Assert.Null(report.GrantScript);
        Assert.Equal("All 4 permissions SqlVitals reads with are there.", report.Summary);
    }

    [Fact]
    public void ABareLogin_MissesAllFour_AndEachSaysWhichPagesItAffects()
    {
        var report = PermissionCheck.Evaluate(Probe(serverState: false, anyDefinition: false, readable: []));

        Assert.Equal(
            [PermissionArea.ServerState, PermissionArea.BackupHistory, PermissionArea.AgentJobs, PermissionArea.Definitions],
            report.Missing.Select(r => r.Area));
        Assert.Equal("4 of 4 permissions are missing.", report.Summary);
        Assert.Contains("Overview", Area(report, PermissionArea.ServerState).Pages);
        Assert.Contains("Monitoring", Area(report, PermissionArea.ServerState).Also, StringComparison.Ordinal);
        Assert.Equal(["Backups"], Area(report, PermissionArea.BackupHistory).Pages);
        Assert.Equal(["Agent Jobs"], Area(report, PermissionArea.AgentJobs).Pages);
        Assert.Equal(["Security Audit"], Area(report, PermissionArea.Definitions).Pages);
        Assert.Equal(PermissionCheck.BackupTables, Area(report, PermissionArea.BackupHistory).MissingTables);
        Assert.Equal(PermissionCheck.JobTables, Area(report, PermissionArea.AgentJobs).MissingTables);
    }

    [Fact]
    public void SomeAgentTablesReadable_OnlyTheOthersAreMissing_AndNamed()
    {
        var readable = AllTables.Except(["sysjobactivity", "syssessions"]);

        var jobs = Area(PermissionCheck.Evaluate(Probe(readable: readable)), PermissionArea.AgentJobs);

        Assert.Equal(PermissionState.Missing, jobs.State);
        Assert.Equal(["sysjobactivity", "syssessions"], jobs.MissingTables);
        Assert.Contains("msdb.dbo.sysjobactivity and msdb.dbo.syssessions", jobs.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void OneMissing_IsSaidInTheSingular()
    {
        var report = PermissionCheck.Evaluate(Probe(anyDefinition: false));

        Assert.Equal("1 of 4 permissions is missing.", report.Summary);
    }

    [Fact]
    public void Express_HasNoAgent_SoItsTablesAreNotNeeded()
    {
        var report = PermissionCheck.Evaluate(Probe(Express, readable: PermissionCheck.BackupTables));

        var jobs = Area(report, PermissionArea.AgentJobs);
        Assert.Equal(PermissionState.NotNeeded, jobs.State);
        Assert.Contains("Express", jobs.Note, StringComparison.Ordinal);
        Assert.True(report.AllGranted);
        Assert.Equal("All 3 permissions SqlVitals reads with are there.", report.Summary);
    }

    [Fact]
    public void AzureSqlDatabase_NeedsOnlyViewDatabaseState()
    {
        var report = PermissionCheck.Evaluate(Probe(AzureDb, serverState: false, anyDefinition: false, readable: []));

        var state = Assert.Single(report.Missing);
        Assert.Equal(PermissionArea.ServerState, state.Area);
        Assert.Equal("VIEW DATABASE STATE", state.Needs);
        Assert.Contains("Sales", state.Note, StringComparison.Ordinal);
        Assert.All(report.Results.Where(r => r.Area != PermissionArea.ServerState),
                   r => Assert.Equal(PermissionState.NotNeeded, r.State));
        Assert.Equal("The permission SqlVitals reads with is missing.", report.Summary);
    }

    [Fact]
    public void ManagedInstance_IsCheckedLikeSqlServer()
    {
        var report = PermissionCheck.Evaluate(Probe(Mi, serverState: false, readable: []));

        Assert.Equal(AzureKind.ManagedInstance, report.Probe.Azure);
        Assert.Equal("VIEW SERVER STATE", Area(report, PermissionArea.ServerState).Needs);
        Assert.Equal(3, report.Missing.Count);
    }

    // ── The GRANT script ─────────────────────────────────────────────────

    [Fact]
    public void SqlServer_GrantsInMaster_ThenAUserAndSelectsInMsdb()
    {
        var script = PermissionCheck.Evaluate(Probe(serverState: false, anyDefinition: false, readable: ["sysjobs"])).GrantScript!;

        Assert.Contains(string.Join(Environment.NewLine, "USE [master];", "GRANT VIEW SERVER STATE TO [CONTOSO\\svc_vitals];",
                                    "GRANT VIEW ANY DEFINITION TO [CONTOSO\\svc_vitals];"),
                        script, StringComparison.Ordinal);
        Assert.Contains("USE [msdb];", script, StringComparison.Ordinal);
        Assert.Contains("IF DATABASE_PRINCIPAL_ID(N'CONTOSO\\svc_vitals') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("CREATE USER [CONTOSO\\svc_vitals] FOR LOGIN [CONTOSO\\svc_vitals];", script, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON dbo.backupset TO [CONTOSO\\svc_vitals];", script, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON dbo.sysjobhistory TO [CONTOSO\\svc_vitals];", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.sysjobs ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE LOGIN", script, StringComparison.Ordinal);
        Parses(script);
    }

    [Fact]
    public void OnlyMsdbMissing_HasNoMasterPart()
    {
        var script = PermissionCheck.Evaluate(Probe(readable: PermissionCheck.JobTables)).GrantScript!;

        Assert.DoesNotContain("USE [master]", script, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON dbo.backupmediafamily", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sysjobs", script, StringComparison.Ordinal);
        Parses(script);
    }

    [Fact]
    public void OnlyServerGrantsMissing_HasNoMsdbPart()
    {
        var script = PermissionCheck.Evaluate(Probe(serverState: false)).GrantScript!;

        Assert.Contains("GRANT VIEW SERVER STATE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("msdb", script, StringComparison.Ordinal);
        Parses(script);
    }

    [Fact]
    public void ManagedInstance_GetsTheSameScriptAsSqlServer()
    {
        Assert.Equal(PermissionCheck.Evaluate(Probe(SqlServer, serverState: false, readable: [])).GrantScript,
                     PermissionCheck.Evaluate(Probe(Mi, serverState: false, readable: [])).GrantScript);
    }

    [Fact]
    public void AzureSqlDatabase_GrantsToTheDatabaseUser_WithoutUse()
    {
        var script = PermissionCheck.Evaluate(Probe(AzureDb, serverState: false)).GrantScript!;

        Assert.Contains("GRANT VIEW DATABASE STATE TO [vitals];", script, StringComparison.Ordinal);
        Assert.Contains("Run it in Sales", script, StringComparison.Ordinal);
        Assert.DoesNotContain("USE ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("msdb", script, StringComparison.Ordinal);
        Parses(script);
    }

    [Fact]
    public void ALoginThroughAWindowsGroup_IsToldToGrantToTheGroup()
    {
        var script = PermissionCheck.Evaluate(Probe(serverState: false, loginIsOwn: false)).GrantScript!;

        Assert.Contains("signs in through a Windows group", script, StringComparison.Ordinal);
        Assert.Contains("-- CREATE LOGIN [CONTOSO\\svc_vitals] FROM WINDOWS;", script, StringComparison.Ordinal);
        Parses(script);
    }

    [Fact]
    public void NamesAreQuoted_SoABracketOrQuoteCantEndThem()
    {
        var script = PermissionCheck.Evaluate(Probe(readable: [], login: "o'brien]x")).GrantScript!;

        Assert.Contains("TO [o'brien]]x];", script, StringComparison.Ordinal);
        Assert.Contains("DATABASE_PRINCIPAL_ID(N'o''brien]x')", script, StringComparison.Ordinal);
        Parses(script);
    }

    // The script is for a DBA to paste into SSMS: it must be valid T-SQL.
    private static void Parses(string script)
    {
        new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(script), out var errors);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => $"{e.Line}: {e.Message}")) + "\n" + script);
    }
}
