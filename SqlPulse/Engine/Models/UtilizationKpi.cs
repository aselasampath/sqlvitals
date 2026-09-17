namespace SqlPulse.Engine.Models;

/// <summary>CPU and memory utilization summary for the server overview utilization panel.</summary>
public record UtilizationKpi(
    // ── CPU (from ring buffer or dm_db_resource_stats on Azure) ──────
    double AvgCpuPct,
    double P95CpuPct,
    double MaxCpuPct,
    int    SampleCount,
    // ── Workers (from sys.dm_os_schedulers) ──────────────────────────
    int    LogicalCpus,
    int    WorkersUsed,
    int    WorkersTotal,
    // ── Memory (from sys.dm_os_performance_counters) ─────────────────
    long   StolenMemKB,          // Stolen Server Memory (KB)
    long   TotalServerMemKB,     // Total Server Memory (KB)
    long   DatabaseCacheMemKB,   // Database Cache Memory (KB) = buffer pool
    long   PhysicalMemoryKB,     // from sys.dm_os_sys_info
    long   TargetMemKB           // committed_target_kb
);
