namespace SqlPulse.Engine.Models;

/// <summary>Application connection summary from sys.dm_exec_sessions.</summary>
public record ApplicationConnection(
    string  ApplicationName,
    int     CurrentConnections,
    int     ActiveRequests,
    long    TotalCpuTimeMs,
    long    TotalMemoryKB,
    long    TotalLogicalReads,
    long    TotalReads,
    long    TotalWrites,
    DateTime FirstLoginTime,
    DateTime LastRequestTime,
    DateTime CaptureTime
);
