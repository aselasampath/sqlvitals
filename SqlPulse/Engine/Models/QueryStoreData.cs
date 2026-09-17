namespace SqlPulse.Engine.Models;

/// <summary>Query Store health/configuration for the current database.</summary>
public record QueryStoreHealth(
    string   ActualState,
    string   DesiredState,
    string?  ReadOnlyReason,
    long     CurrentStorageSizeMB,
    long     MaxStorageSizeMB,
    double   StorageUsedPct,
    int      FlushIntervalSec,
    int      IntervalLengthMin,
    int      StaleQueryThresholdDays,
    string   SizeCleanupMode,
    string   CaptureMode,
    int      MaxPlansPerQuery,
    int      TotalQueries,
    int      TotalPlans,
    int      ForcedPlans,
    DateTime CaptureTime,
    // Derived
    bool     IsEnabled,
    string   Severity   // OK / WARNING (read-only, near capacity)
);

/// <summary>A top query from Query Store (last 24h by CPU).</summary>
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
