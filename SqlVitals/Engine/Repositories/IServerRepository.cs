namespace SqlVitals.Engine.Repositories;

/// <summary>What kind of server the connection is on (#47).</summary>
public interface IServerRepository
{
    // ── Engine edition detection ──────────────────────────────────────
    /// <summary>The server's edition, version and the login's permissions, read once per connection (#167).</summary>
    Task<ServerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<bool> IsAzureSqlDatabaseAsync(CancellationToken cancellationToken = default);
    Task<bool> SupportsOnlineIndexRebuildAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of the permissions SqlVitals reads with the login holds (#177), for the first-run and
    /// Settings check. Always asks the server: a GRANT made since is seen.
    /// </summary>
    Task<Permissions.PermissionProbe> CheckPermissionsAsync(CancellationToken cancellationToken = default);
}
