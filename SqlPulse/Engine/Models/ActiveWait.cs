namespace SqlPulse.Engine.Models;

public record ActiveWait(
    int SessionId,
    int BlockingSessionId,
    string? WaitType,
    long WaitTimeMs,
    double WaitTimeSec,
    string? LastWaitType,
    string? WaitResource,
    string? RequestStatus,
    string? Command,
    long CpuTimeMs,
    long TotalElapsedMs,
    long LogicalReads,
    long Writes,
    long PhysicalReads,
    long GrantedMemoryKB,
    int DegreeOfParallelism,
    string? LoginName,
    string? HostName,
    string? ProgramName,
    string? DatabaseName,
    string WaitCategory,
    int IsBlocked,
    string? QueryText,
    DateTime CaptureTime,
    string WaitSeverity
);
