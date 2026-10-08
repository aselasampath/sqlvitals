namespace SqlVitals.Engine.Repositories;

/// <summary>What kind of server the connection is on: its edition and what it supports. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class ServerRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), IServerRepository
{
    // ── What the server is (#167): read once per connection ───────────

    public Task<ServerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        CapabilitiesAsync(cancellationToken);

    // Azure SQL Database lacks sys.master_files and other server-scoped DMVs used by TempDB
    // file-level reporting (see GetTempDbPressureAsync).
    public async Task<bool> IsAzureSqlDatabaseAsync(CancellationToken cancellationToken = default) =>
        (await CapabilitiesAsync(cancellationToken)).IsAzureSqlDatabase;

    // Online index rebuilds are an edition feature, so the maintenance script only offers
    // WITH (ONLINE = ON) where the server accepts it (see IndexMaintenanceScript).
    public async Task<bool> SupportsOnlineIndexRebuildAsync(CancellationToken cancellationToken = default) =>
        (await CapabilitiesAsync(cancellationToken)).SupportsOnlineIndexRebuild;
}
