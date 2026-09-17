namespace SqlPulse.Engine.Models;

/// <summary>Summary KPI strip: plan cache erased flag, multi-plan queries, total plan count.</summary>
public record PlanCacheHealthSummary(
    bool   PlanCacheErasedRecently,    // DBCC FREEPROCCACHE or restart in last 24 h
    int    MultiPlanQueryCount,        // queries with > 5 plans (parameterisation failure)
    int    TotalCachedPlans,           // total entries in dm_exec_cached_plans
    int    PlanGuidesEnabled,          // plans frozen by sp_create_plan_guide
    int    JoinHintCount,              // cached plans containing join hints
    string Severity                    // OK | WARNING | CRITICAL
);

/// <summary>A query with many distinct plans — indicates parameterisation failure.</summary>
public record MultiPlanQuery(
    int      PlanCount,
    long     TotalExecutions,
    string?  QueryHash,
    string?  QueryText,
    string?  DatabaseName,
    string   Severity               // WARNING if PlanCount > 20, else INFO
);

/// <summary>A query with a RID or Key Lookup in its plan — index gap.</summary>
public record KeyLookupQuery(
    string   LookupType,            // "RID Lookup" or "Key Lookup"
    long     ExecutionCount,
    long     TotalLogicalReads,
    double   AvgLogicalReads,
    string?  DatabaseName,
    string?  ObjectName,
    string?  QueryText
);

/// <summary>A currently-executing query with a large cardinality misestimation.</summary>
public record CardinalityMisestimate(
    int      SessionId,
    int      NodeId,
    long     EstimatedRows,
    long     ActualRows,
    double   MisestimateRatio,      // ActualRows / EstimatedRows (or inverse)
    string?  DatabaseName,
    string?  QueryText,
    string   Severity               // CRITICAL if ratio >= 10 000, else WARNING
);

/// <summary>A currently-executing query with skewed parallelism across threads.</summary>
public record SkewedParallelQuery(
    int      SessionId,
    int      NodeId,
    long     MaxActualRows,
    long     MinActualRows,
    double   SkewRatio,             // MaxRows / NULLIF(MinRows,0)
    string?  DatabaseName,
    string?  QueryText,
    string   Severity               // CRITICAL if SkewRatio >= 10, else WARNING
);

/// <summary>A plan guide that is actively pinning execution plans.</summary>
public record PlanGuide(
    string   GuideName,
    string   GuideType,
    string?  DatabaseName,
    string?  ObjectName,
    string?  QueryText,
    bool     IsDisabled
);
