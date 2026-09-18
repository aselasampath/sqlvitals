namespace SqlVitals.Engine.Models;

/// <summary>Plan cache summary broken down by object type.</summary>
public record PlanCacheTypeSummary(
    string   CacheType,
    int      PlanCount,
    long     TotalSizeMB,
    double   AvgUseCounts,
    int      SingleUsePlans,
    double   SingleUsePct,
    DateTime CaptureTime,
    // Derived
    string   Severity   // WARNING if SingleUsePct > 40
);

/// <summary>A single performance counter from plan cache / SQL Statistics.</summary>
public record PlanCacheCounter(
    string   CounterName,
    long     CounterValue,
    DateTime CaptureTime
);

/// <summary>A top query from the plan cache (dm_exec_query_stats).</summary>
public record PlanCacheTopQuery(
    long     ExecutionCount,
    long     TotalCpuMs,
    long     TotalElapsedMs,
    double   AvgCpuMs,
    double   AvgElapsedMs,
    long     TotalLogicalReads,
    long     TotalPhysicalReads,
    long     TotalLogicalWrites,
    int      PlanGenerations,
    string   PlanType,
    long     PlanSizeKB,
    long     UseCounts,
    DateTime PlanCreationTime,
    string?  DatabaseName,
    string?  QueryText,
    DateTime CaptureTime
);
