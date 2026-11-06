using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Tables and their indexes' sizes and usage (#242), in a database the caller picks, without changing
/// the connection. Reads documented views only, and no table rows. A table under a schema change, or
/// a permission the login lacks, costs only what it hides: the lists say what's missing.
/// </summary>
public interface ITableStatisticsRepository
{
    /// <summary>The online databases the login can open; only the connected one on Azure SQL Database.</summary>
    Task<TableDatabases> GetTableDatabasesAsync(CancellationToken cancellationToken = default);

    /// <summary>A database's user tables, the largest first.</summary>
    /// <param name="database">The database to read; null for the connection's.</param>
    Task<TableSizeList> GetTableSizesAsync(string? database, CancellationToken cancellationToken = default);

    /// <summary>One table's indexes, the largest first.</summary>
    /// <param name="database">The database to read; null for the connection's.</param>
    Task<TableIndexList> GetTableIndexesAsync(string? database, int objectId, CancellationToken cancellationToken = default);
}
