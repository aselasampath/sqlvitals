namespace SqlPulse.Engine.Models;

/// <summary>
/// Represents a SQL Server session for the process-map visualization.
/// VisualState values: "Active" | "HeadBlocker" | "Blocked" | "Sleeping"
/// </summary>
public record ProcessNode(
    int     SessionId,
    int     ParentId,          // 0 = no blocker
    string  Status,
    string? UserName,
    string? HostName,
    string? ProgramName,
    string? WaitType,
    long    WaitTimeMs,
    string? LastWaitType,
    string? WaitResource,
    string? DatabaseName,
    long    CpuTime,
    long    PhysicalIo,
    long    MemoryUsage,
    DateTime LoginTime,
    DateTime LastBatch,
    int     OpenTran,
    string? Command,
    string? CommandText,
    string  VisualState        // Active | HeadBlocker | Blocked | Sleeping
);
