using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Permissions;

/// <summary>
/// What the server answered to the permission check (#177): who the login is there, and which of
/// the permissions SqlVitals reads with it holds. Nothing is asked of the server beyond this.
/// </summary>
/// <param name="EngineEdition">SERVERPROPERTY('EngineEdition'): 4 Express, 5 Azure SQL Database, 8 Managed Instance.</param>
/// <param name="Login">SUSER_SNAME(): the login as the server knows it, which the GRANT names.</param>
/// <param name="DatabaseUser">USER_NAME() in the connection's database: what a GRANT names on Azure SQL Database.</param>
/// <param name="Database">DB_NAME(): the connection's database.</param>
/// <param name="LoginIsOwn">
/// False when the login signs in through a Windows group and has no login of its own: a GRANT to
/// its name would fail, so the script says so.
/// </param>
/// <param name="ServerState">VIEW SERVER STATE; on Azure SQL Database, VIEW DATABASE STATE in the database.</param>
/// <param name="AnyDefinition">VIEW ANY DEFINITION; false on Azure SQL Database, which doesn't need it.</param>
/// <param name="MsdbReadable">The msdb tables (<see cref="PermissionCheck.BackupTables"/>, <see cref="PermissionCheck.JobTables"/>) the login may SELECT.</param>
public sealed record PermissionProbe(
    int                  EngineEdition,
    string               Login,
    string               DatabaseUser,
    string               Database,
    bool                 LoginIsOwn,
    bool                 ServerState,
    bool                 AnyDefinition,
    IReadOnlySet<string> MsdbReadable)
{
    public AzureKind Azure => EngineEdition switch
    {
        ServerCapabilities.AzureSqlDatabaseEdition => AzureKind.SqlDatabase,
        ServerCapabilities.ManagedInstanceEdition  => AzureKind.ManagedInstance,
        _                                          => AzureKind.None,
    };
}

/// <summary>What one group of SqlVitals' reads needs (#177).</summary>
public enum PermissionArea
{
    /// <summary>VIEW SERVER STATE (VIEW DATABASE STATE on Azure SQL Database): monitoring and almost every page.</summary>
    ServerState,

    /// <summary>SELECT on msdb's backup history: Backups, and the Backups past RPO check.</summary>
    BackupHistory,

    /// <summary>SELECT on msdb's SQL Server Agent tables: Agent Jobs, and the Failed Agent jobs check.</summary>
    AgentJobs,

    /// <summary>VIEW ANY DEFINITION: the Security Audit's lists, and every database's files for the Disk free check.</summary>
    Definitions,
}

public enum PermissionState
{
    Granted,
    Missing,

    /// <summary>The server has no such thing to read: msdb on Azure SQL Database, Agent on Express.</summary>
    NotNeeded,
}

/// <summary>One area's result: what it needs, whether the login has it, and what goes without it.</summary>
/// <param name="Needs">"VIEW SERVER STATE", "SELECT on msdb.dbo.backupset and backupmediafamily".</param>
/// <param name="Pages">The pages that need it, by their titles in the sidebar.</param>
/// <param name="Also">What else needs it: monitoring, a health check.</param>
/// <param name="Note">Why it isn't needed, or what goes without it.</param>
/// <param name="MissingTables">The msdb tables it can't read, for the GRANT.</param>
public sealed record PermissionResult(
    PermissionArea        Area,
    PermissionState       State,
    string                Title,
    string                Needs,
    IReadOnlyList<string> Pages,
    string                Also,
    string                Note,
    IReadOnlyList<string> MissingTables)
{
    public bool IsMissing => State == PermissionState.Missing;
}

/// <summary>The check's results for one connection, and the script that grants what's missing.</summary>
public sealed record PermissionReport(PermissionProbe Probe, IReadOnlyList<PermissionResult> Results)
{
    public IReadOnlyList<PermissionResult> Missing => Results.Where(r => r.IsMissing).ToList();

    public bool AllGranted => Missing.Count == 0;

    /// <summary>"All 4 permissions SqlVitals reads with are there.", "2 of 4 permissions are missing."</summary>
    public string Summary
    {
        get
        {
            var needed = Results.Count(r => r.State != PermissionState.NotNeeded);
            var missing = Missing.Count;
            return missing == 0
                ? needed == 1 ? "The permission SqlVitals reads with is there." : $"All {needed} permissions SqlVitals reads with are there."
                : needed == 1 ? "The permission SqlVitals reads with is missing."
                : missing == 1 ? $"1 of {needed} permissions is missing." : $"{missing} of {needed} permissions are missing.";
        }
    }

    /// <summary>The GRANT script for what's missing, for a DBA to run once; null when nothing is.</summary>
    public string? GrantScript => PermissionCheck.GrantScript(this);
}

/// <summary>
/// The first-run and Settings permission check (#177): from what the server answered, which areas
/// lack a permission, which pages that affects, and the GRANT that fixes it, for SQL Server,
/// Managed Instance and Azure SQL Database.
/// </summary>
public static class PermissionCheck
{
    /// <summary>The msdb tables the Backups page and the Backups past RPO check read.</summary>
    public static readonly IReadOnlyList<string> BackupTables = ["backupset", "backupmediafamily"];

    /// <summary>The msdb tables the Agent Jobs page and the Failed Agent jobs check read.</summary>
    public static readonly IReadOnlyList<string> JobTables =
        ["sysjobs", "sysjobhistory", "sysjobactivity", "sysjobschedules", "sysschedules", "syscategories", "syssessions"];

    /// <summary>The pages that read DMVs, by their sidebar titles: all of them need the server's state.</summary>
    public static readonly IReadOnlyList<string> ServerStatePages =
    [
        "Overview", "Live charts", "Processes", "Active Waits", "Deadlocks", "App Connections", "Top Waits",
        "Resource Queries", "Plan Cache Health", "Implicit Conv.", "SP Trace", "Memory Grants", "TempDB",
        "File I/O", "DB Storage", "Perfmon", "Config Checks",
    ];

    public static PermissionReport Evaluate(PermissionProbe probe)
    {
        var azureDb = probe.Azure == AzureKind.SqlDatabase;
        var express = probe.EngineEdition == ServerCapabilities.ExpressEdition;

        var results = new List<PermissionResult>
        {
            new(PermissionArea.ServerState,
                probe.ServerState ? PermissionState.Granted : PermissionState.Missing,
                "Server state",
                azureDb ? "VIEW DATABASE STATE" : "VIEW SERVER STATE",
                ServerStatePages,
                "Monitoring and the health score",
                azureDb ? $"In the database {probe.Database}." : "Without it nothing can be monitored, and most pages fail.",
                []),
            Tables(PermissionArea.BackupHistory, "Backup history", BackupTables, probe,
                   pages: ["Backups"], also: "The Backups past RPO check",
                   notNeeded: azureDb ? "Azure SQL Database takes its own backups; there is no msdb." : null),
            Tables(PermissionArea.AgentJobs, "SQL Server Agent jobs", JobTables, probe,
                   pages: ["Agent Jobs"], also: "The Failed Agent jobs check, and the jobs in an Overview's Since you last looked",
                   notNeeded: azureDb ? "Azure SQL Database has no SQL Server Agent."
                            : express ? "Express has no SQL Server Agent."
                            : null),
            new(PermissionArea.Definitions,
                azureDb ? PermissionState.NotNeeded : probe.AnyDefinition ? PermissionState.Granted : PermissionState.Missing,
                "Definitions",
                "VIEW ANY DEFINITION",
                azureDb ? [] : ["Security Audit"],
                azureDb ? string.Empty : "The Disk free check, which reads every database's files",
                azureDb ? "The Security Audit and the disk check don't run on Azure SQL Database."
                        : "Without it the Security Audit can't list the sysadmin members or orphaned users. It shows definitions, never data.",
                []),
        };
        return new PermissionReport(probe, results);
    }

    private static PermissionResult Tables(PermissionArea area, string title, IReadOnlyList<string> tables, PermissionProbe probe,
                                           IReadOnlyList<string> pages, string also, string? notNeeded)
    {
        var needs = $"SELECT on msdb.dbo.{tables[0]}" + (tables.Count switch
        {
            1 => string.Empty,
            2 => $" and {tables[1]}",
            _ => $" and {tables.Count - 1} more Agent tables",
        });
        if (notNeeded is not null)
            return new(area, PermissionState.NotNeeded, title, needs, pages, also, notNeeded, []);

        var missing = tables.Where(t => !probe.MsdbReadable.Contains(t)).ToList();
        return new(area, missing.Count == 0 ? PermissionState.Granted : PermissionState.Missing, title, needs, pages, also,
                   missing.Count == 0 || missing.Count == tables.Count
                       ? "Read-only: the login can't change, start or delete anything."
                       : $"It can't read {Join(missing.Select(t => "msdb.dbo." + t).ToList())}.",
                   missing);
    }

    /// <summary>
    /// The script that grants what's missing, for the login the connection signs in with; null when
    /// nothing is. On SQL Server and Managed Instance: master's grants, then a user in msdb and its
    /// SELECTs. On Azure SQL Database, which has no USE, the grant to the database's user, run there.
    /// </summary>
    public static string? GrantScript(PermissionReport report)
    {
        var missing = report.Missing;
        if (missing.Count == 0)
            return null;

        var probe = report.Probe;
        var script = new List<string>();
        if (probe.Azure == AzureKind.SqlDatabase)
        {
            var user = Bracket(probe.DatabaseUser);
            script.Add($"-- SqlVitals: what {probe.DatabaseUser} is missing in the database {probe.Database}.");
            script.Add($"-- Run it in {probe.Database}, connected as its owner or an admin (Azure SQL Database has no USE).");
            script.Add($"GRANT VIEW DATABASE STATE TO {user};");
            return string.Join(Environment.NewLine, script);
        }

        var login = Bracket(probe.Login);
        script.Add($"-- SqlVitals: what {probe.Login} is missing. Run it once, as a DBA; it only grants reads.");
        if (!probe.LoginIsOwn)
        {
            script.Add($"-- {probe.Login} signs in through a Windows group and has no login of its own. Grant to the");
            script.Add("-- group's login instead (every member gets it), or create a login for it first:");
            script.Add($"-- CREATE LOGIN {login} FROM WINDOWS;");
        }

        var serverGrants = missing.Where(r => r.Area is PermissionArea.ServerState or PermissionArea.Definitions).ToList();
        if (serverGrants.Count > 0)
        {
            script.Add(string.Empty);
            script.Add("USE [master];");
            foreach (var result in serverGrants)
                script.Add($"GRANT {result.Needs} TO {login};");
        }

        var tables = missing.SelectMany(r => r.MissingTables).ToList();
        if (tables.Count > 0)
        {
            script.Add(string.Empty);
            script.Add("USE [msdb];");
            script.Add($"IF DATABASE_PRINCIPAL_ID({Literal(probe.Login)}) IS NULL");
            script.Add($"    CREATE USER {login} FOR LOGIN {login};");
            foreach (var table in tables)
                script.Add($"GRANT SELECT ON dbo.{table} TO {login};");
        }
        return string.Join(Environment.NewLine, script);
    }

    // A quoted identifier: ] doubled, so a name can't end the brackets early.
    private static string Bracket(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    // A Unicode string literal: ' doubled.
    private static string Literal(string text) => $"N'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
