using System.Globalization;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Repositories;

/// <summary>Which Azure SQL, if any, the server is.</summary>
public enum AzureKind
{
    /// <summary>SQL Server on a machine or a VM.</summary>
    None,

    /// <summary>Azure SQL Database (EngineEdition 5): one database, no server-scoped DMVs.</summary>
    SqlDatabase,

    /// <summary>Azure SQL Managed Instance (EngineEdition 8): an instance, with most of them.</summary>
    ManagedInstance,
}

/// <summary>
/// What the server is and what the login may do there (#167), read once per connection: the
/// repositories choose their queries by it, rather than each asking SERVERPROPERTY again.
/// </summary>
/// <param name="EngineEdition">SERVERPROPERTY('EngineEdition'): 4 Express, 5 Azure SQL Database, 8 Managed Instance.</param>
/// <param name="MajorVersion">13 for SQL Server 2016, 16 for 2022; Azure SQL reports the version it runs.</param>
/// <param name="Permissions">The login's effective permissions (fn_my_permissions), on the server and in the database.</param>
/// <param name="PermissionsKnown">False when they couldn't be read: then <see cref="Has"/> says yes, and the query tells.</param>
public sealed record ServerCapabilities(int EngineEdition, int MajorVersion, IReadOnlySet<string> Permissions, bool PermissionsKnown = true)
{
    public const int ExpressEdition          = 4;
    public const int AzureSqlDatabaseEdition = 5;
    public const int ManagedInstanceEdition  = 8;

    public AzureKind Azure => EngineEdition switch
    {
        AzureSqlDatabaseEdition => AzureKind.SqlDatabase,
        ManagedInstanceEdition  => AzureKind.ManagedInstance,
        _                       => AzureKind.None,
    };

    /// <summary>Azure SQL Database: no sys.master_files, waits per database, database-scoped event sessions.</summary>
    public bool IsAzureSqlDatabase => Azure == AzureKind.SqlDatabase;

    /// <summary>Whether the login holds a permission ("VIEW SERVER STATE"); yes when they couldn't be read.</summary>
    public bool Has(string permission) => !PermissionsKnown || Permissions.Contains(permission);

    /// <summary>
    /// sys.dm_exec_query_memory_grants has queue_id, wait_order and is_next_candidate: SQL Server
    /// 2016 (13) and later, and both Azure SQLs.
    /// </summary>
    public bool HasMemoryGrantQueue => MajorVersion >= 13 || Azure != AzureKind.None;

    /// <summary>Online index rebuilds are an edition feature (<see cref="IndexMaintenanceScript"/>).</summary>
    public bool SupportsOnlineIndexRebuild => IndexMaintenanceScript.EditionSupportsOnlineRebuild(EngineEdition);

    /// <summary>The capabilities from what the server answered.</summary>
    /// <param name="productVersion">SERVERPROPERTY('ProductVersion'): "16.0.4135.4".</param>
    /// <param name="permissions">The permission names; null when they couldn't be read.</param>
    public static ServerCapabilities From(int engineEdition, string? productVersion, IEnumerable<string>? permissions) =>
        new(engineEdition,
            int.TryParse(productVersion?.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0,
            permissions?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PermissionsKnown: permissions is not null);
}

/// <summary>
/// One connection's <see cref="ServerCapabilities"/>, read once and shared by its repositories
/// (#167). A permission failure forgets them, so a GRANT made meanwhile is seen on the next read.
/// </summary>
public sealed class ServerCapabilitiesCache
{
    private readonly object _gate = new();
    private Task<ServerCapabilities>? _read;

    /// <summary>
    /// The capabilities, read the first time. The read itself isn't cancelled, since others may
    /// share it; a caller that is cancelled stops waiting.
    /// </summary>
    public Task<ServerCapabilities> GetAsync(Func<Task<ServerCapabilities>> read, CancellationToken cancellationToken)
    {
        Task<ServerCapabilities>? task;
        lock (_gate)
        {
            task = _read;
            if (task is null)
            {
                _read = task = read();
                // A failed read isn't kept: the next caller tries again. (One that failed at once
                // runs this here, inside the lock, which is why the task is held in a local.)
                task.ContinueWith(Drop, CancellationToken.None,
                                  TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                                  TaskScheduler.Default);
            }
        }
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Reads them again next time: the login's permissions may have changed.</summary>
    public void Forget()
    {
        lock (_gate)
            _read = null;
    }

    private void Drop(Task<ServerCapabilities> failed)
    {
        lock (_gate)
            if (ReferenceEquals(_read, failed))
                _read = null;
    }
}
