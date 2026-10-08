using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Database storage and configuration (#47).</summary>
public interface IStorageRepository
{
    // ── Database storage & configuration ───────────────────────────
    Task<(
        IEnumerable<ServerConfigParam> Config,
        IEnumerable<DatabaseFile> Files,
        IEnumerable<TempDbFileSummary> TempDbFiles,
        IEnumerable<TableStorage> TopTables
    )> GetDatabaseStorageAsync(CancellationToken cancellationToken = default);
}
