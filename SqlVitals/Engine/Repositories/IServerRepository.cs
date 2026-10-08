namespace SqlVitals.Engine.Repositories;

/// <summary>What kind of server the connection is on (#47).</summary>
public interface IServerRepository
{
    // ── Engine edition detection ──────────────────────────────────────
    /// <summary>The server's edition, version and the login's permissions, read once per connection (#167).</summary>
    Task<ServerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<bool> IsAzureSqlDatabaseAsync(CancellationToken cancellationToken = default);
    Task<bool> SupportsOnlineIndexRebuildAsync(CancellationToken cancellationToken = default);
}
