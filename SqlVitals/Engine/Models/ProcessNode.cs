namespace SqlVitals.Engine.Models;

/// <summary>
/// Represents a SQL Server session for the process-map visualization.
/// VisualState values: "Active" | "HeadBlocker" | "Blocked" | "Sleeping", set by
/// <see cref="Monitoring.BlockingChains.Classify"/>.
/// </summary>
public record ProcessNode(
    int     SessionId,
    int     ParentId,          // blocking_session_id: 0 = no blocker, negative = not a session
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
