namespace SqlVitals.Engine.Models;

/// <summary>Point-in-time snapshot of all key SQL Server memory KPIs.</summary>
public record MemorySnapshot(
    long   PhysicalMemoryKB,
    long   TotalServerMemoryKB,          // Total Server Memory (KB)
    long   TargetServerMemoryKB,         // Target Server Memory (KB)
    long   DatabaseCacheMemoryKB,        // Database Cache Memory (KB) — Buffer Pool data pages
    long   SqlCacheMemoryKB,             // SQL Cache Memory (KB) — plan cache
    long   GrantedWorkspaceMemoryKB,     // Granted Workspace Memory (KB) — active sorts/hashes
    long   FreeMemoryKB,
    long   MemoryGrantsPending,
    long   MemoryGrantsOutstanding,
    long   AvailablePhysicalKB,
    long   TotalPageFileKB,
    long   AvailablePageFileKB,
    string SystemMemoryState,
    string MemoryModel,
    DateTime CaptureTime
);
