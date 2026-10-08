using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Repositories;

/// <summary>What kind of server the connection is on: its edition and what it supports. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class ServerRepository(IConfiguration configuration) : BaseRepository(configuration), IServerRepository
{
    // ── Engine edition detection ──────────────────────────────────────
    // EngineEdition 5 = Azure SQL Database, which lacks sys.master_files and other
    // server-scoped DMVs used by TempDB file-level reporting (see GetTempDbPressureAsync).
    public async Task<bool> IsAzureSqlDatabaseAsync()
    {
        using var conn = CreateConnection();
        var row = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition");
        return row?.Edition == 5;
    }

    // Online index rebuilds are an edition feature, so the maintenance script only offers
    // WITH (ONLINE = ON) where the server accepts it (see IndexMaintenanceScript).
    public async Task<bool> SupportsOnlineIndexRebuildAsync()
    {
        using var conn = CreateConnection();
        var row = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition");
        return row is not null && IndexMaintenanceScript.EditionSupportsOnlineRebuild(Convert.ToInt32(row.Edition));
    }
}
