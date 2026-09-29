namespace SqlVitals.Engine.QueryStore;

/// <summary>What Query Store's runtime statistics measure per execution, and what the page ranks by (#111).</summary>
public enum QueryStoreMetric
{
    Cpu,
    Duration,
    Executions,
    LogicalReads,
    LogicalWrites,
    PhysicalReads,
    Memory,
    Rows,
    LogUsed,
    TempDbUsed,
}

/// <summary>How one query's executions in the period are summed up into one figure.</summary>
public enum QueryStoreStatistic { Total, Average, Max, StdDev }

/// <summary>
/// One metric: the sys.query_store_runtime_stats columns behind it (<c>avg_</c>, <c>min_</c>,
/// <c>max_</c> and <c>stdev_</c> + <see cref="Column"/>), and how its raw unit is shown.
/// </summary>
/// <param name="Column">The column suffix; null for Executions, which is count_executions itself.</param>
/// <param name="Unit">"ms", "MB" or empty for a plain count.</param>
/// <param name="Scale">Multiplies the raw value into <paramref name="Unit"/>: µs to ms, 8 KB pages or bytes to MB.</param>
/// <param name="NeedsSql2017">Only SQL Server 2017+ and Azure SQL have the column.</param>
public sealed record QueryStoreMetricInfo(
    QueryStoreMetric Metric, string Name, string? Column, string Unit, double Scale, bool NeedsSql2017)
{
    /// <summary>"Duration ms", "Logical reads", "Memory MB".</summary>
    public string NameWithUnit => Unit.Length == 0 ? Name : $"{Name} {Unit}";

    /// <summary>Plain counts are shown without decimals; ms and MB with one.</summary>
    public string Format => Unit.Length == 0 ? "N0" : "N1";
}

public static class QueryStoreMetrics
{
    private const double PagesToMb = 8.0 / 1024;

    /// <summary>High Variation lists only queries run this often in the period: one that ran twice isn't "variable".</summary>
    public const int MinVariationExecutions = 10;

    public static IReadOnlyList<QueryStoreMetricInfo> All { get; } =
    [
        new(QueryStoreMetric.Cpu,           "CPU",            "cpu_time",              "ms", 0.001,                 false),
        new(QueryStoreMetric.Duration,      "Duration",       "duration",              "ms", 0.001,                 false),
        new(QueryStoreMetric.Executions,    "Executions",     null,                    "",   1,                     false),
        new(QueryStoreMetric.LogicalReads,  "Logical reads",  "logical_io_reads",      "",   1,                     false),
        new(QueryStoreMetric.LogicalWrites, "Logical writes", "logical_io_writes",     "",   1,                     false),
        new(QueryStoreMetric.PhysicalReads, "Physical reads", "physical_io_reads",     "",   1,                     false),
        new(QueryStoreMetric.Memory,        "Memory",         "query_max_used_memory", "MB", PagesToMb,             false),
        new(QueryStoreMetric.Rows,          "Rows",           "rowcount",              "",   1,                     false),
        new(QueryStoreMetric.LogUsed,       "Log used",       "log_bytes_used",        "MB", 1.0 / (1024 * 1024),   true),
        new(QueryStoreMetric.TempDbUsed,    "TempDB used",    "tempdb_space_used",     "MB", PagesToMb,             true),
    ];

    /// <summary>The metrics High Variation offers, as SSMS's Queries With High Variation does.</summary>
    public static IReadOnlyList<QueryStoreMetric> VariationMetrics { get; } =
        [QueryStoreMetric.Duration, QueryStoreMetric.Cpu, QueryStoreMetric.LogicalReads, QueryStoreMetric.Memory, QueryStoreMetric.Rows];

    /// <summary>The Over Time charts shown until the user picks others.</summary>
    public static IReadOnlyList<QueryStoreMetric> DefaultChartMetrics { get; } =
        [QueryStoreMetric.Duration, QueryStoreMetric.Cpu, QueryStoreMetric.LogicalReads, QueryStoreMetric.Executions];

    public static QueryStoreMetricInfo Info(QueryStoreMetric metric) => All.First(m => m.Metric == metric);

    /// <summary>The metrics a server has: log and TempDB only on SQL Server 2017+ and Azure SQL.</summary>
    public static IReadOnlyList<QueryStoreMetricInfo> Available(bool hasSql2017Metrics) =>
        All.Where(m => hasSql2017Metrics || !m.NeedsSql2017).ToList();

    /// <summary>"Total", "Avg", "Max", "Std dev".</summary>
    public static string Label(QueryStoreStatistic statistic) => statistic switch
    {
        QueryStoreStatistic.Average => "Avg",
        QueryStoreStatistic.Max     => "Max",
        QueryStoreStatistic.StdDev  => "Std dev",
        _                           => "Total",
    };

    /// <summary>"Total CPU ms", "Avg Duration ms", "Executions" (which is always a total).</summary>
    public static string ColumnHeader(QueryStoreMetric metric, QueryStoreStatistic statistic)
    {
        var info = Info(metric);
        return metric == QueryStoreMetric.Executions ? info.Name : $"{Label(statistic)} {info.NameWithUnit}";
    }

    /// <summary>
    /// Parses saved metric names, keeping known ones once in their saved order. Nothing usable gives
    /// the defaults.
    /// </summary>
    public static IReadOnlyList<QueryStoreMetric> ParseChartMetrics(IEnumerable<string>? names)
    {
        var parsed = (names ?? [])
            .Select(n => Enum.TryParse<QueryStoreMetric>(n, ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : (QueryStoreMetric?)null)
            .OfType<QueryStoreMetric>()
            .Distinct()
            .ToList();
        return parsed.Count == 0 ? DefaultChartMetrics : parsed;
    }
}
