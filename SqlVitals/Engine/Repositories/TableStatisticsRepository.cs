using System.Data;
using System.Globalization;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Table Statistics' reads (#242). Each runs through the chosen database's <c>sp_executesql</c>, so
/// the one connection string serves them all and nothing about the connection changes. Which views
/// wait on a schema change was checked on SQL Server 2017-2025; the .sql files say what was found.
/// </summary>
public sealed class TableStatisticsRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null)
    : BaseRepository(target, capabilities), ITableStatisticsRepository
{
    private const string LockedList = "which tables are locked";
    private const string SizeList   = TableReadFailure.Sizes;
    private const string UsageList  = "index reads and writes";
    private const string StartTime  = "when the server restarted";
    private const string StatisticsRead = "table statistics";

    // What "lacks SELECT" means here, and what else would do: a monitoring login may well not want SELECT.
    private const string WhySelect = " SQL Server shows a table's statistics only to a login with SELECT on their columns, or that owns the table, or in db_owner or db_ddladmin.";

    private readonly string? _login = target.Login;

    public async Task<TableDatabases> GetTableDatabasesAsync(CancellationToken cancellationToken = default)
    {
        var isAzure = (await CapabilitiesAsync(cancellationToken)).IsAzureSqlDatabase;
        using var conn = CreateConnection();
        var rows = (await Q(conn, SqlScripts.Get("TableStatistics/Databases"), cancellationToken)).ToList();
        var connected = await ConnectedAsync(conn, cancellationToken);
        var names = rows.Select(r => (string)r.Name).ToList();
        return isAzure
            ? new TableDatabases(connected, [connected], false)
            : new TableDatabases(connected, names.Contains(connected) ? names : [.. names, connected], true);
    }

    public async Task<TableSizeList> GetTableSizesAsync(string? database, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var failures = new List<TableReadFailure>();

        var tables = (await In(conn, database, SqlScripts.Get("TableStatistics/Tables"), cancellationToken))
            .Select(r => ((int)r.ObjectId, (string)r.SchemaName, (string)r.TableName)).ToList();

        if (!await SeesAllTablesAsync(conn, database, cancellationToken))
            failures.Add(new TableReadFailure(TableReadFailure.Names, Explained(new PermissionNeeded("VIEW DEFINITION", PermissionScope.Database, await NamedAsync(conn, database, cancellationToken)), out var fix), fix));

        var sizeRows = await PartlyAsync(conn, database, SqlScripts.Get("TableStatistics/TableSizes"), null, SizeList, DatabaseState, failures, cancellationToken);
        var lockRows = await PartlyAsync(conn, database, SqlScripts.Get("TableStatistics/LockedTables"), null, LockedList, ServerState, failures, cancellationToken);

        var sizes = sizeRows?.ToDictionary(r => (int)r.ObjectId, r => ((long)r.RowsInTable, (long)r.DataPages, (long)r.IndexPages));
        var locked = (lockRows ?? []).Select(r => (int)r.ObjectId).ToHashSet();
        return new TableSizeList(TableSpace.Merge(tables, sizes, locked), failures);
    }

    public async Task<TableIndexList> GetTableIndexesAsync(string? database, int objectId, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var failures = new List<TableReadFailure>();

        var indexes = (await In(conn, database, SqlScripts.Get("TableStatistics/Indexes"), cancellationToken, objectId)).ToList();

        // This read waits for the table's lock, for no longer than its LOCK_TIMEOUT.
        IReadOnlyList<dynamic>? sizeRows = null;
        var isLocked = false;
        try
        {
            sizeRows = (await In(conn, database, SqlScripts.Get("TableStatistics/IndexSizes"), cancellationToken, objectId)).ToList();
        }
        catch (WaitStatsException ex) when (ex.Failure.Kind == SqlFailureKind.LockTimeout)
        {
            isLocked = true;
        }
        catch (WaitStatsException ex) when (ex.Failure.IsPermission)
        {
            failures.Add(await FailureAsync(conn, SizeList, ex, DatabaseState, database, cancellationToken));
        }

        var usageRows = isLocked ? null : await PartlyAsync(conn, database, SqlScripts.Get("TableStatistics/IndexUsage"), objectId, UsageList, ServerState, failures, cancellationToken);
        var started = await ServerStartedAsync(conn, database, failures, cancellationToken);

        var sizes = sizeRows?.ToDictionary(r => (int)r.IndexId);
        var usage = usageRows?.ToDictionary(r => (int)r.IndexId);
        var list = indexes.Select(i =>
            {
                int id = i.IndexId;
                dynamic? size = sizes?.GetValueOrDefault(id);
                dynamic? use = usage?.GetValueOrDefault(id);
                return new TableIndexSize(id, (string?)i.IndexName, (string)i.IndexType,
                    (bool)i.IsUnique || (bool)i.IsPrimaryKey || (bool)i.IsUniqueConstraint,
                    sizes is null ? null : size is null ? 0L : (long)size.RowsInIndex,
                    sizes is null ? null : size is null ? 0L : (long)size.ReservedPages,
                    sizes is null ? null : size is null ? 0L : (long)size.UsedPages,
                    usage is null ? null : use is null ? 0L : (long)use.Reads,
                    usage is null ? null : use is null ? 0L : (long)use.Writes);
            })
            .OrderByDescending(i => i.ReservedPages ?? -1).ThenBy(i => i.IndexId)
            .ToList();
        return new TableIndexList(list, isLocked, started, failures);
    }

    public async Task<TableHealthBatch> GetTableStatisticsHealthAsync(string? database, IReadOnlyList<int> objectIds, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var failures = new List<TableReadFailure>();
        var level = await CompatibilityLevelAsync(conn, database, failures, cancellationToken);

        var tables = new List<TableStatisticsHealth>(objectIds.Count);
        foreach (var objectId in objectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tables.Add(await TableHealthAsync(conn, database, objectId, level, cancellationToken));
        }

        // One failure however many tables: the GRANT is the same, so the page says it once.
        if (tables.Any(t => t.State == StatisticsHealthState.NotReadable))
            failures.Add(new TableReadFailure(StatisticsRead,
                Explained(new PermissionNeeded("SELECT", PermissionScope.Database, await NamedAsync(conn, database, cancellationToken)), out var fix) + WhySelect, fix));
        return new TableHealthBatch(tables, failures);
    }

    private async Task<TableStatisticsHealth> TableHealthAsync(IDbConnection conn, string? database, int objectId, int? level, CancellationToken cancellationToken)
    {
        try
        {
            var rows = (await In(conn, database, SqlScripts.Get("TableStatistics/TableHealth"), cancellationToken, objectId)).ToList();
            var properties = rows.Select(r => (StatisticsProperties?)PropertiesOf(r)).ToList();

            // No row reads the same as never built, so that's when to ask whether the login may read them.
            var neverBuilt = rows.Where((_, i) => properties[i] is null).Select(r => (int)r.StatsId).ToHashSet();
            if (neverBuilt.Count > 0 && (await NotReadableAsync(conn, database, objectId, cancellationToken)).Any(r => neverBuilt.Contains((int)r.StatsId)))
                return new TableStatisticsHealth(objectId, StatisticsHealthState.NotReadable, null, null, null);

            return StatisticsFindings.RateTable(objectId, properties, level);
        }
        catch (WaitStatsException ex) when (ex.Failure.Kind == SqlFailureKind.LockTimeout)
        {
            return new TableStatisticsHealth(objectId, StatisticsHealthState.Locked, null, null, null);
        }
        catch (WaitStatsException ex) when (ex.Failure.Kind == SqlFailureKind.MissingPermission)
        {
            return new TableStatisticsHealth(objectId, StatisticsHealthState.NotReadable, null, null, null);
        }
    }

    public async Task<TableStatisticsList> GetTableStatisticsAsync(string? database, int objectId, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var rows = (await In(conn, database, SqlScripts.Get("TableStatistics/Statistics"), cancellationToken, objectId))
            .Select(r => new StatisticsObject((int)r.StatsId, (string)r.Name, (bool)r.IsIndex, (bool)r.IsClustered, (bool)r.IsFiltered, (string?)r.LeadingColumn))
            .ToList();
        return new TableStatisticsList(rows, await IsLockedAsync(conn, database, objectId, cancellationToken), []);
    }

    public async Task<StatisticsDetails> GetStatisticsDetailsAsync(string? database, int objectId, int statsId, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var failures = new List<TableReadFailure>();
        var isLocked = false;

        // A part that can't be read costs only that part. Once one finds the table locked, the rest would just wait too.
        async Task<IReadOnlyList<dynamic>> PartAsync(string sql, string what)
        {
            if (isLocked)
                return [];
            try
            {
                return (await In(conn, database, sql, cancellationToken, objectId, statsId)).ToList();
            }
            catch (WaitStatsException ex) when (ex.Failure.Kind == SqlFailureKind.LockTimeout)
            {
                isLocked = true;
                return [];
            }
            catch (WaitStatsException ex)
            {
                failures.Add(await FailureAsync(conn, what, ex, SelectInDatabase, database, cancellationToken));
                return [];
            }
        }

        var level = await CompatibilityLevelAsync(conn, database, failures, cancellationToken);
        var header = await PartAsync(SqlScripts.Get("TableStatistics/StatisticsProperties"), StatisticsRead);
        var properties = header.Select(r => (StatisticsProperties?)PropertiesOf(r)).FirstOrDefault();

        // No header reads the same as never built, so that's when to ask whether the login may read it.
        if (properties is null && !isLocked && await WhyNotReadableAsync(conn, database, objectId, statsId, cancellationToken) is { } denied)
            return new StatisticsDetails(null, [], [], level, false, [.. failures, denied]);

        var histogram = await PartAsync(SqlScripts.Get("TableStatistics/StatisticsHistogram"), "the histogram");
        var density = await PartAsync(SqlScripts.Get("TableStatistics/StatisticsDensity"), "the density vector");

        return new StatisticsDetails(properties,
            histogram.Select(r => new HistogramStep((int)(long)r.Number, (string?)r.RangeHiKey, Real(r.RangeRows), Real(r.EqRows), Real(r.DistinctRangeRows), Real(r.AvgRangeRows))).ToList(),
            density.Select(r => DensityOf(r)).OfType<DensityEntry>().ToList(),
            level, isLocked, failures);
    }

    // Never built: the function has no row, or one with nothing in it.
    private static StatisticsProperties? PropertiesOf(dynamic row) =>
        row.LastUpdated is null && row.RowsInStats is null
            ? null
            : new StatisticsProperties((DateTime?)row.LastUpdated, (long?)row.RowsInStats ?? 0, (long?)row.RowsSampled ?? 0, (int?)row.Steps ?? 0, (long?)row.Modifications ?? 0);

    // The real columns' types can differ between versions.
    private static double Real(object? value) => value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    // DBCC names its columns with spaces. A prefix of statistics never built has no density.
    private static DensityEntry? DensityOf(dynamic row)
    {
        var columns = (IDictionary<string, object?>)row;
        return columns["All density"] is { } density && columns["Columns"] is string names
            ? new DensityEntry(names, Real(density), Real(columns["Average Length"]))
            : null;
    }

    private async Task<int?> CompatibilityLevelAsync(IDbConnection conn, string? database, List<TableReadFailure> failures, CancellationToken cancellationToken)
    {
        var rows = await PartlyAsync(conn, database, SqlScripts.Get("TableStatistics/CompatibilityLevel"), null, "the compatibility level", DatabaseState, failures, cancellationToken);
        return rows is { Count: > 0 } ? (int)rows[0].CompatibilityLevel : null;
    }

    private async Task<IReadOnlyList<dynamic>> NotReadableAsync(IDbConnection conn, string? database, int objectId, CancellationToken cancellationToken) =>
        (await In(conn, database, SqlScripts.Get("TableStatistics/StatisticsNotReadable"), cancellationToken, objectId)).ToList();

    // Names the one table in the GRANT, so a DBA doesn't have to open up the whole database for it.
    private async Task<TableReadFailure?> WhyNotReadableAsync(IDbConnection conn, string? database, int objectId, int statsId, CancellationToken cancellationToken)
    {
        var denied = (await NotReadableAsync(conn, database, objectId, cancellationToken)).FirstOrDefault(r => (int)r.StatsId == statsId);
        if (denied is null)
            return null;
        var permission = new PermissionNeeded("SELECT", PermissionScope.OnObject, await NamedAsync(conn, database, cancellationToken), (string)denied.SchemaName, (string)denied.TableName);
        return new TableReadFailure(StatisticsRead, Explained(permission, out var fix) + WhySelect, fix);
    }

    // The list's names don't wait, so it asks. Without VIEW SERVER STATE, the read of the details still finds out.
    private async Task<bool> IsLockedAsync(IDbConnection conn, string? database, int objectId, CancellationToken cancellationToken)
    {
        try
        {
            return (await In(conn, database, SqlScripts.Get("TableStatistics/LockedTables"), cancellationToken)).Any(r => (int)r.ObjectId == objectId);
        }
        catch (WaitStatsException ex) when (ex.Failure.IsPermission)
        {
            return false;
        }
    }

    private static PermissionNeeded SelectInDatabase(string? database, ServerCapabilities server) =>
        new("SELECT", PermissionScope.Database, database);

    // Runs one of the area's queries in the database chosen, or as it is in the connection's.
    private Task<IEnumerable<dynamic>> In(IDbConnection conn, string? database, string sql, CancellationToken cancellationToken, int? objectId = null, int? statsId = null) =>
        database is null
            ? Q(conn, sql, cancellationToken, new { ObjectId = objectId, StatsId = statsId })
            : Q(conn, SqlScripts.Get("TableStatistics/InDatabase"), cancellationToken, new { Database = database, Sql = sql, ObjectId = objectId, StatsId = statsId });

    // A read the page can do without: what it couldn't read goes in failures, and the rest goes on.
    private async Task<IReadOnlyList<dynamic>?> PartlyAsync(IDbConnection conn, string? database, string sql, int? objectId, string what,
                                                            Func<string?, ServerCapabilities, PermissionNeeded> needed,
                                                            List<TableReadFailure> failures, CancellationToken cancellationToken)
    {
        try
        {
            return (await In(conn, database, sql, cancellationToken, objectId)).ToList();
        }
        catch (WaitStatsException ex) when (ex.Failure.IsPermission || ex.Failure.Kind == SqlFailureKind.LockTimeout)
        {
            failures.Add(await FailureAsync(conn, what, ex, needed, database, cancellationToken));
            return null;
        }
    }

    private async Task<DateTime?> ServerStartedAsync(IDbConnection conn, string? database, List<TableReadFailure> failures, CancellationToken cancellationToken)
    {
        try
        {
            return (DateTime?)(await QFirst(conn, SqlScripts.Get("TableStatistics/ServerStart"), cancellationToken))?.ServerStartTime;
        }
        catch (WaitStatsException ex) when (ex.Failure.IsPermission)
        {
            failures.Add(await FailureAsync(conn, StartTime, ex, ServerState, database, cancellationToken));
            return null;
        }
    }

    private static PermissionNeeded DatabaseState(string? database, ServerCapabilities server) =>
        new("VIEW DATABASE STATE", PermissionScope.Database, database);

    // What SqlVitals asks of the server: on Azure SQL Database, the database holds it.
    private static PermissionNeeded ServerState(string? database, ServerCapabilities server) =>
        server.IsAzureSqlDatabase ? DatabaseState(database, server) : new("VIEW SERVER STATE", PermissionScope.Server);

    // Error 297 doesn't name the permission, so what this read needs is named instead of guessed.
    private async Task<TableReadFailure> FailureAsync(IDbConnection conn, string what, WaitStatsException error,
                                                      Func<string?, ServerCapabilities, PermissionNeeded> needed,
                                                      string? database, CancellationToken cancellationToken)
    {
        var failure = error.Failure;
        if (failure.Kind == SqlFailureKind.MissingPermission && failure.Permission is null or { Inferred: true })
            failure = failure with { Permission = needed(await NamedAsync(conn, database, cancellationToken), await CapabilitiesAsync(cancellationToken)) };
        return new TableReadFailure(what, failure.Explanation ?? error.Message, failure.FixScript(_login));
    }

    private string Explained(PermissionNeeded permission, out string? fix)
    {
        var failure = new SqlFailure(SqlFailureKind.MissingPermission, 0, permission, permission.Database);
        fix = failure.FixScript(_login);
        return failure.Explanation!;
    }

    private async Task<bool> SeesAllTablesAsync(IDbConnection conn, string? database, CancellationToken cancellationToken)
    {
        var row = (await In(conn, database, SqlScripts.Get("TableStatistics/SeesAllTables"), cancellationToken)).FirstOrDefault();
        return row is null || Convert.ToInt32(row.SeesAllTables) == 1;
    }

    // The database a GRANT goes in: the one chosen, or the connection's, which on Azure SQL Database is the only one.
    private async Task<string> NamedAsync(IDbConnection conn, string? database, CancellationToken cancellationToken) =>
        database ?? await ConnectedAsync(conn, cancellationToken);

    private async Task<string> ConnectedAsync(IDbConnection conn, CancellationToken cancellationToken) =>
        (string)(await QFirst(conn, "SELECT DB_NAME() AS Connected", cancellationToken))!.Connected;
}
