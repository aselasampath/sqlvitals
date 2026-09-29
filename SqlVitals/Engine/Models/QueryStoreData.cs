using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Models;

/// <summary>Query Store's settings and size for the current database (sys.database_query_store_options).</summary>
public record QueryStoreHealth(
    string    DatabaseName,
    string    ActualState,
    string    DesiredState,
    int       ReadOnlyReasonCode,
    long      CurrentStorageSizeMB,
    long      MaxStorageSizeMB,
    double    StorageUsedPct,
    int       FlushIntervalSec,
    int       IntervalLengthMin,
    int       StaleQueryThresholdDays,
    string    SizeCleanupMode,
    string    CaptureMode,
    string?   WaitStatsCaptureMode,   // null before SQL Server 2017
    int       MaxPlansPerQuery,
    int       TotalQueries,
    int       TotalPlans,
    int       ForcedPlans,
    DateTime? DataFromUtc,            // start of the oldest interval Query Store still holds
    DateTime  CaptureTime)
{
    /// <summary>Why Query Store went read-only, in words; null when it didn't.</summary>
    public string? ReadOnlyReason => QueryStoreChecks.DescribeReadOnlyReason(ReadOnlyReasonCode);

    /// <summary>READ_WRITE, READ_ONLY or READ_CAPTURE_SECONDARY: what it holds can be read.</summary>
    public bool IsEnabled => ActualState.StartsWith("READ", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A top query from Query Store (last 24h by CPU), for Export for AI.</summary>
public record QueryStoreTopQuery(
    long     QueryId,
    long     PlanId,
    string   QueryText,
    long     TotalExecutions,
    double   TotalCpuMs,
    double   AvgCpuMs,
    double   TotalDurationMs,
    double   AvgDurationMs,
    long     TotalLogicalReads,
    double   AvgLogicalReads,
    long     TotalLogicalWrites,
    long     TotalPhysicalReads,
    double   TotalMemoryMB,
    double   AvgMemoryKB,
    bool     IsForcedPlan,
    string   PlanType,
    DateTime LastExecutionTimeUtc,
    DateTime CaptureTime,
    // Derived
    string   Severity   // based on CPU and reads
);

/// <summary>
/// What this server's Query Store has beyond SQL Server 2016's, asked of sys.all_columns once per
/// connection (#111). Tabs and metrics a server lacks are hidden or explained, never errors.
/// </summary>
public sealed record QueryStoreFeatures(
    bool WaitStats,             // sys.query_store_wait_stats: 2017+
    bool WaitStatsCaptureMode,  // database_query_store_options.wait_stats_capture_mode_desc: 2017+
    bool LogBytes,              // runtime_stats.avg_log_bytes_used: 2017+
    bool TempDb,                // runtime_stats.avg_tempdb_space_used: 2017+
    bool ForcingType,           // plan.plan_forcing_type_desc: 2017+
    bool PlanType,              // plan.plan_type_desc: 2022+
    bool Hints)                 // sys.query_store_query_hints: 2022+
{
    public static QueryStoreFeatures None { get; } = new(false, false, false, false, false, false, false);

    /// <summary>Log used and TempDB used can be ranked and charted.</summary>
    public bool Sql2017Metrics => LogBytes && TempDb;
}

/// <summary>What the page opens with: the database, and Query Store's settings when the server has it.</summary>
public sealed record QueryStoreStatus(string DatabaseName, QueryStoreHealth? Health, QueryStoreFeatures Features)
{
    /// <summary>SQL Server 2016+: the catalog views exist.</summary>
    public bool IsSupported => Health is not null;

    /// <summary>Its data can be read: READ_WRITE or READ_ONLY. OFF and ERROR can't be relied on.</summary>
    public bool IsAvailable => Health is { IsEnabled: true };

    /// <summary>What each tab says instead of an empty grid; null when Query Store can be read.</summary>
    public string? UnavailableReason => Health switch
    {
        null                   => "This SQL Server has no Query Store: it came with SQL Server 2016.",
        { IsEnabled: true }    => null,
        { ActualState: "ERROR" } => $"Query Store is in an error state for {DatabaseName}. The settings checks above say how to recover it.",
        _                      => $"Query Store is off for {DatabaseName}.",
    };

    /// <summary>The statement to copy that turns it on; null unless it's off.</summary>
    public string? EnableScript => Health is { IsEnabled: false, ActualState: not "ERROR" }
        ? $"ALTER DATABASE {UnusedIndexDropScript.QuoteName(DatabaseName)} SET QUERY_STORE = ON;"
        : null;
}

/// <summary>A tab's rows, or why it has none: a view or column this version lacks (error 208 or 207).</summary>
public sealed record QueryStoreRows<T>(IReadOnlyList<T> Rows, string? Unavailable = null)
{
    public static QueryStoreRows<T> Missing(string why) => new([], why);
}

/// <summary>
/// Totals of every metric over some executions: Σ(avg × count_executions) in the raw units Query
/// Store keeps (µs, 8 KB pages, bytes).
/// </summary>
public sealed record QueryStoreTotals(
    long   Executions,
    double CpuUs,
    double DurationUs,
    double LogicalReads,
    double LogicalWrites,
    double PhysicalReads,
    double MemoryPages,
    double Rows,
    double LogBytes,
    double TempDbPages)
{
    public static QueryStoreTotals Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>The metric's total in the unit it's shown in (ms, MB or a count).</summary>
    public double Total(QueryStoreMetric metric) => metric == QueryStoreMetric.Executions
        ? Executions
        : Raw(metric) * QueryStoreMetrics.Info(metric).Scale;

    /// <summary>Per execution, in the unit it's shown in; Executions is the count itself. Null without executions.</summary>
    public double? Average(QueryStoreMetric metric) =>
        metric == QueryStoreMetric.Executions ? Executions
        : Executions > 0 ? Total(metric) / Executions
        : null;

    public QueryStoreTotals Add(QueryStoreTotals o) => new(
        Executions + o.Executions, CpuUs + o.CpuUs, DurationUs + o.DurationUs, LogicalReads + o.LogicalReads,
        LogicalWrites + o.LogicalWrites, PhysicalReads + o.PhysicalReads, MemoryPages + o.MemoryPages,
        Rows + o.Rows, LogBytes + o.LogBytes, TempDbPages + o.TempDbPages);

    private double Raw(QueryStoreMetric metric) => metric switch
    {
        QueryStoreMetric.Cpu           => CpuUs,
        QueryStoreMetric.Duration      => DurationUs,
        QueryStoreMetric.LogicalReads  => LogicalReads,
        QueryStoreMetric.LogicalWrites => LogicalWrites,
        QueryStoreMetric.PhysicalReads => PhysicalReads,
        QueryStoreMetric.Memory        => MemoryPages,
        QueryStoreMetric.Rows          => Rows,
        QueryStoreMetric.LogUsed       => LogBytes,
        QueryStoreMetric.TempDbUsed    => TempDbPages,
        _                              => Executions,
    };
}

/// <summary>Top Queries: one query's executions in the period, over all its plans.</summary>
public sealed record QueryStoreQueryRow(
    long      QueryId,
    long      ObjectId,
    string?   ObjectName,
    string    QueryText,
    int       Plans,
    long      Executions,
    long      FailedExecutions,   // Aborted + Exception
    double    MetricValue,        // the ranked metric and statistic, in its display unit
    double    TotalCpuMs,
    double    AvgDurationMs,
    double    TotalLogicalReads,
    long?     ForcedPlanId,
    long?     TopPlanId,          // the plan with the most executions in the period
    DateTime? LastExecutionUtc)
{
    public string ObjectText    { get; } = QueryStoreText.ObjectLabel(ObjectId, ObjectName);
    public string QueryTextLine { get; } = QueryStoreText.OneLine(QueryText);
    public string QueryTextTip  => QueryStoreText.Tooltip(QueryText);
    public bool   HasSeveralPlans => Plans > 1;
    public string ForcedText    => ForcedPlanId is { } id ? $"Plan {id}" : string.Empty;
    public DateTime? LastExecutionLocal => LastExecutionUtc?.ToLocalTime();

    /// <summary>The page's CPU rule, whatever the ranking: over 60 s of CPU is Critical, over 10 s Warning.</summary>
    public string Severity { get; } = TotalCpuMs > 60_000 ? "Critical" : TotalCpuMs > 10_000 ? "Warning" : "Info";
}

/// <summary>High Variation: how much one query's executions differed, in the metric's display unit.</summary>
public sealed record QueryStoreVariationRow(
    long    QueryId,
    long    ObjectId,
    string? ObjectName,
    string  QueryText,
    int     Plans,
    long    Executions,
    double  Avg,
    double  Min,
    double  Max,
    double  StdDev,   // combined over every interval and plan (see CombinedStdDev)
    long?   TopPlanId)
{
    public string ObjectText    { get; } = QueryStoreText.ObjectLabel(ObjectId, ObjectName);
    public string QueryTextLine { get; } = QueryStoreText.OneLine(QueryText);
    public string QueryTextTip  => QueryStoreText.Tooltip(QueryText);
    public bool   HasSeveralPlans => Plans > 1;

    /// <summary>Std dev ÷ avg: 3.2 means executions typically differ by over three times the average.</summary>
    public double? Variation => Avg > 0 ? StdDev / Avg : null;
}

/// <summary>Waits: one wait category's total in the period.</summary>
public sealed record QueryStoreWaitCategory(int Category, string Name, double WaitMs, double PctOfAll)
{
    public string Explanation => QueryStoreWaitCategories.Explanation(Category);
}

/// <summary>Waits: one query's wait time in a category.</summary>
public sealed record QueryStoreWaitQuery(
    long    QueryId,
    long    ObjectId,
    string? ObjectName,
    string  QueryText,
    double  WaitMs,
    double  CategoryWaitMs,
    long    Executions,
    long?   TopPlanId)
{
    public string ObjectText    { get; } = QueryStoreText.ObjectLabel(ObjectId, ObjectName);
    public string QueryTextLine { get; } = QueryStoreText.OneLine(QueryText);
    public string QueryTextTip  => QueryStoreText.Tooltip(QueryText);
    public double? AvgWaitMs    => Executions > 0 ? WaitMs / Executions : null;
    public double PctOfCategory => CategoryWaitMs > 0 ? WaitMs * 100 / CategoryWaitMs : 0;
}

/// <summary>Forced Plans: a plan forced by hand or by automatic plan correction, as it is now.</summary>
public sealed record QueryStoreForcedPlan(
    long      QueryId,
    long      PlanId,
    long      ObjectId,
    string?   ObjectName,
    string    QueryText,
    string    ForcedBy,
    long      ForceFailures,
    string?   LastFailureReason,
    DateTime? LastExecutionUtc,
    long      Executions,         // of this plan, in the period
    double?   AvgDurationMs)
{
    public string ObjectText    { get; } = QueryStoreText.ObjectLabel(ObjectId, ObjectName);
    public string QueryTextLine { get; } = QueryStoreText.OneLine(QueryText);
    public string QueryTextTip  => QueryStoreText.Tooltip(QueryText);
    public DateTime? LastExecutionLocal => LastExecutionUtc?.ToLocalTime();

    /// <summary>The last reason forcing failed, e.g. NO_INDEX; empty when it never failed.</summary>
    public string LastFailureText => ForceFailures > 0 && LastFailureReason is { Length: > 0 } r && r != "NONE" ? r : string.Empty;

    /// <summary>Forcing that fails leaves the query on whatever plan the optimizer picks.</summary>
    public string Severity => ForceFailures > 0 ? "Warning" : "OK";

    public string UnforceScript => QueryStoreText.UnforceScript(QueryId, PlanId);
}

/// <summary>Forced Plans: a Query Store hint (SQL Server 2022+ and Azure SQL).</summary>
public sealed record QueryStoreHint(long QueryId, string HintText, string Source, long Failures, string? LastFailureReason, string QueryText)
{
    public string QueryTextLine { get; } = QueryStoreText.OneLine(QueryText);
    public string QueryTextTip  => QueryStoreText.Tooltip(QueryText);
    public string LastFailureText => Failures > 0 && LastFailureReason is { Length: > 0 } r && r != "NONE" ? r : string.Empty;
}

/// <summary>The Forced Plans tab: the plans, and the hints where the version has them (null otherwise).</summary>
public sealed record QueryStoreForcedPlans(QueryStoreRows<QueryStoreForcedPlan> Plans, QueryStoreRows<QueryStoreHint>? Hints);

/// <summary>Over Time: the database's totals in one slot.</summary>
public sealed record QueryStoreSlot(DateTime StartUtc, QueryStoreTotals Totals);

/// <summary>Query details: the statement.</summary>
public sealed record QueryStoreQueryInfo(long QueryId, long ObjectId, string? ObjectName, string QueryText)
{
    public string ObjectText => QueryStoreText.ObjectLabel(ObjectId, ObjectName);
}

/// <summary>Query details: one plan's executions in one interval.</summary>
public sealed record QueryStorePlanInterval(
    DateTime  StartUtc,
    long      PlanId,
    long      Failed,
    QueryStoreTotals Totals,
    DateTime? FirstExecutionUtc,
    DateTime? LastExecutionUtc);

/// <summary>Query details: whether a plan is forced now, and how forcing it went.</summary>
public sealed record QueryStorePlanState(long PlanId, bool IsForced, long ForceFailures, string? LastForceFailureReason);

/// <summary>Query details: one row of the plans grid.</summary>
public sealed record QueryStorePlanSummary(
    long      PlanId,
    DateTime? FirstUsedUtc,
    DateTime? LastUsedUtc,
    long      Executions,
    double?   AvgDurationMs,
    double?   AvgCpuMs,
    double?   AvgReads,
    long      Failed,
    bool      IsForced,
    long      ForceFailures,
    string?   LastForceFailureReason)
{
    public DateTime? FirstUsedLocal => FirstUsedUtc?.ToLocalTime();
    public DateTime? LastUsedLocal  => LastUsedUtc?.ToLocalTime();
    public string ForcedText        => IsForced ? "Forced" : string.Empty;
}

/// <summary>Query details: everything the window shows; null query when Query Store doesn't have it.</summary>
public sealed record QueryStoreQueryDetails(
    QueryStoreQueryInfo? Query,
    IReadOnlyList<QueryStorePlanInterval> Intervals,
    IReadOnlyList<QueryStorePlanState> Plans)
{
    public IReadOnlyList<QueryStorePlanSummary> Summaries => QueryStorePlanSummaries.From(Intervals, Plans);
}
