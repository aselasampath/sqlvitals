namespace SqlVitals.Engine.Repositories;

/// <summary>What kind of server the connection is on (#47).</summary>
public interface IServerRepository
{
    // ── Engine edition detection ──────────────────────────────────────
    Task<bool> IsAzureSqlDatabaseAsync();
    Task<bool> SupportsOnlineIndexRebuildAsync();
}
