namespace SqlVitals.Engine.Models;

/// <summary>
/// Work one query did with one plan in one of the two periods a regression check compares.
/// <see cref="QueryKey"/> is the Query Store query_id, or the query_hash ("0x…") from the
/// monitoring history. <see cref="PlanId"/> is the Query Store plan_id; the history has none.
/// </summary>
public sealed record QueryPeriodStats(
    string QueryKey, bool IsRecent, long? PlanId, long Executions, double DurationUs, double CpuUs);

/// <summary>
/// A query whose average duration or CPU per execution rose past the threshold: the baseline
/// and recent figures side by side. Averages are milliseconds per execution. A change is null
/// when the baseline average was zero, so no percentage can be given.
/// </summary>
public sealed record QueryRegression(
    string  QueryKey,
    long    BaselineExecutions,
    long    RecentExecutions,
    double  BaselineAvgDurationMs,
    double  RecentAvgDurationMs,
    double? DurationChangePct,
    double  BaselineAvgCpuMs,
    double  RecentAvgCpuMs,
    double? CpuChangePct,
    bool    DurationRegressed,
    bool    CpuRegressed,
    long?   BaselinePlanId,
    long?   RecentPlanId,
    bool    NewPlan,
    double  ExtraTimeMs)
{
    /// <summary>Filled in after detection, for the rows that are shown only.</summary>
    public string? QueryText    { get; init; }

    /// <summary>Filled in after detection; the history is server-wide, Query Store one database.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>"Duration", "CPU" or "Duration, CPU".</summary>
    public string RegressedOn => (DurationRegressed, CpuRegressed) switch
    {
        (true, true) => "Duration, CPU",
        (true, _)    => "Duration",
        _            => "CPU",
    };

    /// <summary>The plan used most changed between the periods; only Query Store knows plans.</summary>
    public bool PlanDiffers => BaselinePlanId is not null && RecentPlanId is not null && BaselinePlanId != RecentPlanId;

    /// <summary>
    /// "New plan" when the plan used most recently never ran in the baseline, "Different plan"
    /// when it did but another was used most then, otherwise empty.
    /// </summary>
    public string PlanChange => NewPlan ? "New plan" : PlanDiffers ? "Different plan" : string.Empty;
}

/// <summary>
/// What Query Store offers the regression check for the connection's database. When it isn't
/// available (off, or a SQL Server without it) <see cref="Stats"/> is empty and
/// <see cref="State"/> says why.
/// </summary>
public sealed record QueryStoreRegressionStats(
    string DatabaseName, string State, bool IsAvailable, IReadOnlyList<QueryPeriodStats> Stats);
