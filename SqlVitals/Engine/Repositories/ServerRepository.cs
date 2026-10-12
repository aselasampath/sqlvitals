using System.Globalization;
using SqlVitals.Engine.Permissions;

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

    // ── The permission check (#177) ───────────────────────────────────

    public async Task<PermissionProbe> CheckPermissionsAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var row = await QFirst(conn, SqlScripts.Get("Server/PermissionCheck"), cancellationToken)
                  ?? throw new InvalidOperationException("The server didn't answer the permission check.");

        var readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tables = (IDictionary<string, object?>)row;
        foreach (var table in PermissionCheck.BackupTables.Concat(PermissionCheck.JobTables))
            if (Yes(tables.Single(c => c.Key.Equals(table, StringComparison.OrdinalIgnoreCase)).Value))
                readable.Add(table);

        return new PermissionProbe(
            EngineEdition: Convert.ToInt32(row.Edition, CultureInfo.InvariantCulture),
            Login:         (string?)row.LoginName ?? string.Empty,
            DatabaseUser:  (string?)row.UserName ?? string.Empty,
            Database:      (string?)row.DatabaseName ?? string.Empty,
            LoginIsOwn:    Yes(row.LoginIsOwn),
            ServerState:   Yes(row.ServerState),
            AnyDefinition: Yes(row.AnyDefinition),
            MsdbReadable:  readable);
    }

    // HAS_PERMS_BY_NAME's 1; NULL (not asked) is no.
    private static bool Yes(object? value) =>
        value is not null and not DBNull && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
}
