using System.Globalization;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Repositories;

/// <summary>Indexes and statistics: missing and unused indexes, usage, fragmentation and stale statistics. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class IndexRepository(IConfiguration configuration) : BaseRepository(configuration), IIndexRepository
{
    // ── 11. Index health ──────────────────────────────────────────────
    public async Task<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)> GetIndexHealthAsync()
    {
        var missingSql = SqlScripts.Get("Index/Missing");

        var unusedSql = SqlScripts.Get("Index/Unused");

        using var conn = CreateConnection();
        var missing = (await Q(conn, missingSql)).Select(r => new MissingIndex(
            (string)r.DatabaseName, (string)r.SchemaName, (string)r.TableName,
            (string?)r.EqualityColumns, (string?)r.InequalityColumns, (string?)r.IncludedColumns,
            (long)r.UniqueCompiles, (long)r.UserSeeks, (long)r.UserScans,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan,
            (double)r.AvgTotalUserCost, (double)r.AvgUserImpact, (double)r.ImpactScore,
            (string)r.Severity, (string)r.CreateIndexStatement, (DateTime)r.CaptureTime));

        var unused = (await Q(conn, unusedSql)).Select(r => new UnusedIndex(
            (string)r.DatabaseName, (string)r.SchemaName, (string)r.TableName,
            (string)r.IndexName, (string)r.IndexType,
            (bool)r.IsUnique, (bool)r.IsPrimaryKey, (bool)r.IsUniqueConstraint,
            (long)r.UserSeeks, (long)r.UserScans, (long)r.UserLookups, (long)r.UserUpdates,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan, (DateTime?)r.LastUserUpdate,
            (long)r.TotalReads, (long)r.TableRows, (DateTime)r.CaptureTime));

        return (missing, unused);
    }

    // ── 15. Index usage patterns ──────────────────────────────────────
    public async Task<IEnumerable<IndexUsagePattern>> GetIndexUsagePatternsAsync()
    {
        var sql = SqlScripts.Get("Index/UsagePatterns");

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r => new IndexUsagePattern(
            (string)r.TableName, (string?)r.IndexName, (string)r.IndexType,
            (long)r.UserSeeks, (long)r.UserScans, (long)r.UserLookups, (long)r.UserUpdates,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan,
            (DateTime?)r.LastUserLookup, (DateTime?)r.LastUserUpdate,
            (string)r.IndexHealth, (string)r.DatabaseName, (DateTime)r.CaptureTime));
    }

    // ── 16. Index fragmentation ───────────────────────────────────────
    public async Task<IEnumerable<IndexFragmentation>> GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000)
    {
        var sql = SqlScripts.Get("Index/Fragmentation");

        using var conn = CreateConnection();
        return (await Q(conn, sql, new { MinFrag = minFragmentationPercent, MinPageCount = minPageCount })).Select(r =>
        {
            var row = (IDictionary<string, object>)r;
            object? Get(string k) => row.TryGetValue(k, out var v) && v is not DBNull ? v : null;
            return new IndexFragmentation(
                (string)r.SchemaName, (string)r.TableName, (string?)r.IndexName, (string)r.IndexType,
                Convert.ToDouble(Get("FragmentationPercent") ?? 0.0, CultureInfo.InvariantCulture),
                Convert.ToInt64(Get("PageCount") ?? 0L, CultureInfo.InvariantCulture),
                Convert.ToDouble(Get("AvgPageSpaceUsed") ?? 0.0, CultureInfo.InvariantCulture),
                Convert.ToInt64(Get("RecordCount") ?? 0L, CultureInfo.InvariantCulture),
                (string)r.DatabaseName, (string)r.RecommendedAction, (DateTime)r.CaptureTime);
        });
    }

    // ── 18. Stale statistics ──────────────────────────────────────────
    // A statistic built on an empty table and never updated has no date and no row counts, so
    // those read as null. sys.stats.is_incremental is SQL Server 2014+; before that it is 0.
    internal static string StaleStatisticsSql(bool hasIncremental) =>
        StaleStatisticsSqlTemplate.Replace("/*INCREMENTAL*/", hasIncremental ? "s.is_incremental" : "CAST(0 AS BIT)");

    public async Task<IEnumerable<StaleStatistic>> GetStaleStatisticsAsync()
    {
        using var conn = CreateConnection();
        var sql = StaleStatisticsSql(await HasColumnAsync(conn, "sys.stats", "is_incremental"));
        return (await Q(conn, sql)).Select(r => new StaleStatistic(
            (string)r.SchemaName, (string)r.TableName, (string)r.StatisticsName,
            (DateTime?)r.LastUpdated, (int?)r.DaysOld,
            (long?)r.RowsInTable, (long?)r.RowsSampled, (double?)r.SamplePct ?? 0,
            (long)r.ModificationsSinceLastUpdate,
            (bool)r.IsIncremental, (bool)r.AutoCreated, (bool)r.UserCreated,
            (string)r.DatabaseName, (DateTime)r.CaptureTime));
    }

    private static readonly string StaleStatisticsSqlTemplate = SqlScripts.Get("Index/StaleStatistics");
}
