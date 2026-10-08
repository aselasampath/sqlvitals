using System.Text.RegularExpressions;

namespace SqlVitals.Engine.Errors;

/// <summary>What kind of failure a query met, by SQL Server's error number (#167).</summary>
public enum SqlFailureKind
{
    /// <summary>Anything not told apart below: the error's own message says what.</summary>
    Other,

    /// <summary>The login lacks a permission (229, 262, 297, 300): <see cref="SqlFailure.Permission"/> names it.</summary>
    MissingPermission,

    /// <summary>The login has no user in the database (916): <see cref="SqlFailure.Database"/> names it.</summary>
    NoDatabaseAccess,

    /// <summary>The query waited <c>LOCK_TIMEOUT</c> for a lock and gave up (1222).</summary>
    LockTimeout,

    /// <summary>The query ran past its command timeout (-2).</summary>
    Timeout,
}

/// <summary>Where a missing permission is granted.</summary>
public enum PermissionScope
{
    /// <summary>On the server, in master: VIEW SERVER STATE.</summary>
    Server,

    /// <summary>In a database: VIEW DATABASE STATE.</summary>
    Database,

    /// <summary>On one object: SELECT on msdb.dbo.sysjobs.</summary>
    OnObject,
}

/// <summary>A permission a query needed and the login lacks.</summary>
/// <param name="Name">"VIEW SERVER STATE", "SELECT".</param>
/// <param name="Inferred">
/// Error 297 doesn't name the permission: SqlVitals' reads need VIEW SERVER STATE, so that is the
/// likely one.
/// </param>
public sealed record PermissionNeeded(string Name, PermissionScope Scope, string? Database = null,
                                       string? Schema = null, string? ObjectName = null, bool Inferred = false);

/// <summary>
/// A failed query, classified (#167): a missing permission with the <c>GRANT</c> that fixes it, a
/// database the login can't enter, a lock it waited too long for, or a timeout.
/// </summary>
/// <param name="Number">SQL Server's error number; 0 when it wasn't a SQL error.</param>
public sealed partial record SqlFailure(SqlFailureKind Kind, int Number, PermissionNeeded? Permission = null, string? Database = null)
{
    public const int LockTimeoutError = 1222;
    public const int CommandTimeoutError = -2;

    /// <summary>Not a SQL error, or nothing to tell apart.</summary>
    public static SqlFailure None { get; } = new(SqlFailureKind.Other, 0);

    /// <summary>The login can't do what the query needed: a GRANT or a user fixes it.</summary>
    public bool IsPermission => Kind is SqlFailureKind.MissingPermission or SqlFailureKind.NoDatabaseAccess;

    /// <summary>Classifies an error by its number, and reads what it names from its message.</summary>
    public static SqlFailure Classify(int number, string message) => number switch
    {
        LockTimeoutError    => new(SqlFailureKind.LockTimeout, number),
        CommandTimeoutError => new(SqlFailureKind.Timeout, number),
        229 when ObjectDenied().Match(message) is { Success: true } m =>
            Denied(number, new(m.Groups["perm"].Value, PermissionScope.OnObject, m.Groups["db"].Value, m.Groups["schema"].Value, m.Groups["obj"].Value)),
        300 when OnObjectDenied().Match(message) is { Success: true } m =>
            Denied(number, m.Groups["obj"].Value.Equals("server", StringComparison.OrdinalIgnoreCase)
                ? new(m.Groups["perm"].Value, PermissionScope.Server)
                : new(m.Groups["perm"].Value, PermissionScope.Database, m.Groups["db"].Value)),
        262 when InDatabaseDenied().Match(message) is { Success: true } m =>
            Denied(number, new(m.Groups["perm"].Value, PermissionScope.Database, m.Groups["db"].Value)),
        297 => Denied(number, new("VIEW SERVER STATE", PermissionScope.Server, Inferred: true)),
        916 when NoAccess().Match(message) is { Success: true } m =>
            new(SqlFailureKind.NoDatabaseAccess, number, Database: m.Groups["db"].Value),
        229 or 262 or 300 or 916 => new(SqlFailureKind.MissingPermission, number),
        _ => new(SqlFailureKind.Other, number),
    };

    private static SqlFailure Denied(int number, PermissionNeeded permission) =>
        new(SqlFailureKind.MissingPermission, number, permission, permission.Database);

    /// <summary>What went wrong in plain words; null when the error's own message says it best.</summary>
    public string? Explanation => Kind switch
    {
        SqlFailureKind.MissingPermission when Permission is { } p =>
            $"The login lacks {p.Name}{On(p)}{(p.Inferred ? " (most likely: the server didn't say which)" : "")}.",
        SqlFailureKind.MissingPermission => "The login lacks a permission this needs.",
        SqlFailureKind.NoDatabaseAccess  => $"The login has no user in {Database ?? "the database"}.",
        SqlFailureKind.LockTimeout       => "It waited for a lock another session holds, and gave up.",
        SqlFailureKind.Timeout           => "The server took longer than the query timeout.",
        _                                => null,
    };

    private static string On(PermissionNeeded p) => p.Scope switch
    {
        PermissionScope.Server => "",
        PermissionScope.OnObject => $" on {p.Database}.{p.Schema}.{p.ObjectName}",
        _                      => p.Database is null ? "" : $" in {p.Database}",
    };

    /// <summary>
    /// The script that fixes a permission failure, for a DBA to run once; null for any other
    /// failure, or a permission the server didn't name.
    /// </summary>
    /// <param name="login">The login SqlVitals connects as; a placeholder when unknown (Entra, a token).</param>
    public string? FixScript(string? login)
    {
        var who = Bracket(string.IsNullOrWhiteSpace(login) ? "<SqlVitals login>" : login);
        return Kind switch
        {
            SqlFailureKind.MissingPermission when Permission is { Scope: PermissionScope.Server } p =>
                $"USE [master];\nGRANT {p.Name} TO {who};",
            SqlFailureKind.MissingPermission when Permission is { Scope: PermissionScope.Database } p =>
                $"USE {Bracket(p.Database ?? "<database>")};\nGRANT {p.Name} TO {who};",
            SqlFailureKind.MissingPermission when Permission is { Scope: PermissionScope.OnObject } p =>
                $"USE {Bracket(p.Database ?? "<database>")};\nGRANT {p.Name} ON {Bracket(p.Schema ?? "dbo")}.{Bracket(p.ObjectName ?? "<object>")} TO {who};",
            SqlFailureKind.NoDatabaseAccess =>
                $"USE {Bracket(Database ?? "<database>")};\nCREATE USER {who} FOR LOGIN {who};",
            _ => null,
        };
    }

    // A quoted identifier: ] doubled, so a name can't end the brackets early.
    private static string Bracket(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    // 229: The SELECT permission was denied on the object 'sysjobs', database 'msdb', schema 'dbo'.
    [GeneratedRegex(@"The (?<perm>[A-Z][A-Z ]*?) permission was denied on the object '(?<obj>[^']*)', database '(?<db>[^']*)', schema '(?<schema>[^']*)'")]
    private static partial Regex ObjectDenied();

    // 300: VIEW SERVER STATE permission was denied on object 'server', database 'master'.
    [GeneratedRegex(@"(?<perm>[A-Z][A-Z ]*?) permission was denied on object '(?<obj>[^']*)', database '(?<db>[^']*)'")]
    private static partial Regex OnObjectDenied();

    // 262: VIEW DATABASE STATE permission denied in database 'Sales'.
    [GeneratedRegex(@"(?<perm>[A-Z][A-Z ]*?) permission denied in database '(?<db>[^']*)'")]
    private static partial Regex InDatabaseDenied();

    // 916: The server principal "app" is not able to access the database "msdb" under the current security context.
    [GeneratedRegex(@"is not able to access the database ""(?<db>[^""]*)""")]
    private static partial Regex NoAccess();
}
