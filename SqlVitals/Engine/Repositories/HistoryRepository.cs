using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

public class HistoryRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), IHistoryRepository
{
    // Same idle waits the Live Metrics total leaves out (see GetLiveMetricsAsync). They grow
    // every interval on an idle server and say nothing about a problem.
    internal static readonly string BenignWaits =SqlScripts.Get("History/BenignWaits");

    // EngineEdition 5 = Azure SQL Database: no sys.master_files, and waits are per database.
    private bool? _isAzureSqlDb;

    public async Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries, CancellationToken cancellationToken = default)
    {
        var clockSql = SqlScripts.Get("History/Clock");

        var filesSql = SqlScripts.Get("History/Files");

        var filesAzureSql = SqlScripts.Get("History/FilesAzure");

        // One row per query_hash: a statement recompiled or cached with several plans is still
        // one query to the DBA. The HAVING keeps each 5-minute read to what actually ran.
        var queriesSql = SqlScripts.Get("History/Queries");

        var memorySql = SqlScripts.Get("History/Memory");

        var countersSql = SqlScripts.Get("History/PerfmonCounters")
            .Replace("/*COUNTER_FILTER*/", LiveMetricsRepository.PerfmonCounterFilter);

        using var conn = CreateConnection();

        var clock = await QFirst(conn, clockSql, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to read the server clock.");
        _isAzureSqlDb ??= Convert.ToInt32(clock.Edition) == 5;
        var isAzure = _isAzureSqlDb.Value;

        var waitsSql = SqlScripts.Get("History/Waits")
            .Replace("/*WAIT_STATS*/", isAzure ? "sys.dm_db_wait_stats" : "sys.dm_os_wait_stats")
            .Replace("/*BENIGN_WAITS*/", BenignWaits);

        var waits = (await Q(conn, waitsSql, cancellationToken)).Select(r => new WaitTypeTotals(
            (string)r.WaitType, Convert.ToInt64(r.WaitingTasks), Convert.ToInt64(r.WaitTimeMs),
            Convert.ToInt64(r.SignalWaitTimeMs))).ToList();

        var files = (await Q(conn, isAzure ? filesAzureSql : filesSql, cancellationToken)).Select(r => new FileIoTotals(
            (string)r.DatabaseName, Convert.ToInt32(r.FileId), (string)r.FileName, (string)r.FileType,
            Convert.ToInt64(r.Reads), Convert.ToInt64(r.BytesRead), Convert.ToInt64(r.ReadStallMs),
            Convert.ToInt64(r.Writes), Convert.ToInt64(r.BytesWritten), Convert.ToInt64(r.WriteStallMs))).ToList();

        var queries = (await Q(conn, queriesSql, cancellationToken, new { maxQueries, since = queriesExecutedSince })).Select(r => new QueryTotals(
            (string)r.QueryHash, Convert.ToInt64(r.Executions), Convert.ToInt64(r.WorkerTimeUs),
            Convert.ToInt64(r.ElapsedTimeUs), Convert.ToInt64(r.LogicalReads), Convert.ToInt64(r.LogicalWrites),
            (DateTime)r.FirstPlanCreated)).ToList();

        var m = await QFirst(conn, memorySql, cancellationToken);
        HistoryMemory? memory = m?.TotalServerMemoryKB is null ? null : new HistoryMemory(
            Convert.ToInt64(m!.TotalServerMemoryKB),
            Convert.ToInt64(m.TargetServerMemoryKB ?? 0L),
            Convert.ToInt64(m.DatabaseCacheMemoryKB ?? 0L),
            Convert.ToInt64(m.SqlCacheMemoryKB ?? 0L),
            Convert.ToInt64(m.GrantedWorkspaceMemoryKB ?? 0L),
            Convert.ToInt64(m.FreeMemoryKB ?? 0L),
            Convert.ToInt64(m.MemoryGrantsOutstanding ?? 0L));

        // First row per name, as the Perfmon page takes it.
        var counters = (await Q(conn, countersSql, cancellationToken))
            .Select(r => new CounterTotals(
                (string)r.CounterName, Convert.ToInt64(r.CntrValue),
                Convert.ToInt32(r.CntrType) == LiveMetricsRepository.PerfmonRateCounterType))
            .DistinctBy(c => c.CounterName)
            .ToList();

        return new HistoryDetailSnapshot(
            (DateTime)clock.ServerTime, (DateTime)clock.ServerStartTime, waits, files, queries, memory, counters);
    }

    public async Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength, CancellationToken cancellationToken = default)
    {
        if (queryHashes.Count == 0)
            return Array.Empty<QueryTextInfo>();

        var sql = SqlScripts.Get("History/QueryTexts");

        using var conn = CreateConnection();
        return (await Q(conn, sql, cancellationToken, new { hashes = queryHashes, maxLength }))
            .Select(r => new QueryTextInfo((string)r.QueryHash, (string?)r.DatabaseName, (string)r.QueryText))
            .ToList();
    }
}
