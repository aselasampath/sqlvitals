using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>The plan cache's costliest queries, an object's plan, and implicit conversions. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class ResourceQueryRepository(SqlTarget target) : BaseRepository(target), IResourceQueryRepository
{
    // ── 13 + 14. Resource-intensive queries (logical reads + CPU) ─────
    public async Task<(IEnumerable<ResourceIntensiveQuery> ByReads,
                       IEnumerable<ResourceIntensiveQuery> ByCpu,
                       IEnumerable<ResourceIntensiveQuery> ByHighestReads)>
        GetResourceIntensiveQueriesAsync(int topN = 20)
    {
        var readsSql = SqlScripts.Get("ResourceQuery/Reads");

        var cpuSql = SqlScripts.Get("ResourceQuery/Cpu");

        var highestReadsSql = SqlScripts.Get("ResourceQuery/HighestReads");

        static ResourceIntensiveQuery Map(dynamic r) => new(
            (string?)r.QueryText, (string?)r.DatabaseName, (string?)r.ObjectName,
            (long)r.ExecutionCount,
            (long)r.TotalLogicalReads, (long)r.AvgLogicalReads,
            (long)r.TotalCPUTime, (long)r.AvgCPUTime,
            (long)r.TotalElapsedTime, (long)r.AvgElapsedTime,
            (long)r.TotalPhysicalReads, (long)r.TotalLogicalWrites,
            (DateTime)r.LastExecutionTime, (DateTime)r.PlanCreationTime,
            (DateTime)r.CaptureTime, (string?)r.QueryPlan, null, (string?)r.ObjectType);

        static ResourceIntensiveQuery MapHighestReads(dynamic r) => new(
            (string?)r.QueryText, (string?)r.DatabaseName, (string?)r.ObjectName,
            (long)r.ExecutionCount,
            (long)r.TotalLogicalReads, (long)r.AvgLogicalReads,
            (long)r.TotalCPUTime, (long)r.AvgCPUTime,
            (long)r.TotalElapsedTime, (long)r.AvgElapsedTime,
            (long)r.TotalPhysicalReads, (long)r.TotalLogicalWrites,
            (DateTime)r.LastExecutionTime, (DateTime)r.PlanCreationTime,
            (DateTime)r.CaptureTime,
            (string?)r.QueryPlan, (long?)r.LastLogicalReads, (string?)r.ObjectType);

        var p = new { TopN = topN };
        using var conn = CreateConnection();
        // These queries scan dm_exec_query_stats and fetch plans, which can be slow.
        // Increase timeout to 2 minutes to avoid timeouts on busy servers.
        const int extendedTimeout = 120;

        var byReads = (await Q(conn, readsSql, p, extendedTimeout)).Select(r => Map(r));
        var byCpu = (await Q(conn, cpuSql, p, extendedTimeout)).Select(r => Map(r));
        var byHighestReads = (await Q(conn, highestReadsSql, p, extendedTimeout)).Select(r => MapHighestReads(r));
        return (byReads, byCpu, byHighestReads);
    }

    public async Task<string?> GetObjectQueryPlanAsync(string objectName)
    {
        var sql = SqlScripts.Get("ResourceQuery/ObjectQueryPlan");
        using var conn = CreateConnection();
        var r = await QFirst(conn, sql, new { ObjectName = objectName });
        return r is null ? null : (string?)r.QueryPlan;
    }

    // ── 17. Implicit conversions ──────────────────────────────────────
    public async Task<IEnumerable<ImplicitConversion>> GetImplicitConversionsAsync()
    {
        var sql = SqlScripts.Get("ResourceQuery/ImplicitConversions");

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r => new ImplicitConversion(
            (string?)r.DatabaseName, (string?)r.ObjectName,
            (int)r.ExecutionCount, (long)r.PlanSizeKB,
            (string?)r.QueryText, (DateTime)r.CaptureTime));
    }
}
