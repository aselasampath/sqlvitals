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

    /// <summary>
    /// The Statistics health column for these tables (#243), one answer per table in the order asked
    /// for. Reads table by table, so a locked table costs only its own answer: the page asks in
    /// batches, the largest tables first, and shows each as it comes.
    /// </summary>
    /// <param name="database">The database to read; null for the connection's.</param>
    Task<TableHealthBatch> GetTableStatisticsHealthAsync(string? database, IReadOnlyList<int> objectIds, CancellationToken cancellationToken = default);

    /// <summary>One table's statistics objects (#243), the clustered index's first and then by name.</summary>
    /// <param name="database">The database to read; null for the connection's.</param>
    Task<TableStatisticsList> GetTableStatisticsAsync(string? database, int objectId, CancellationToken cancellationToken = default);

    /// <summary>What one statistics object holds (#243): its header, histogram and density vector.</summary>
    /// <param name="database">The database to read; null for the connection's.</param>
    Task<StatisticsDetails> GetStatisticsDetailsAsync(string? database, int objectId, int statsId, CancellationToken cancellationToken = default);
}
