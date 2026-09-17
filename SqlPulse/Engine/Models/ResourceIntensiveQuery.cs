namespace SqlPulse.Engine.Models;

/// <summary>A resource-intensive query from dm_exec_query_stats.</summary>
public record ResourceIntensiveQuery(
    string?  QueryText,
    string?  DatabaseName,
    string?  ObjectName,
    long     ExecutionCount,
    long     TotalLogicalReads,
    long     AvgLogicalReads,
    long     TotalCPUTime,
    long     AvgCPUTime,
    long     TotalElapsedTime,
    long     AvgElapsedTime,
    long     TotalPhysicalReads,
    long     TotalLogicalWrites,
    DateTime LastExecutionTime,
    DateTime PlanCreationTime,
    DateTime CaptureTime,
    string?  QueryPlan        = null,
    long?    LastLogicalReads = null,
    string?  ObjectType       = null
);
