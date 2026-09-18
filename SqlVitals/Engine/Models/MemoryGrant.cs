namespace SqlVitals.Engine.Models;

/// <summary>An active or pending memory grant.</summary>
public record MemoryGrant(
    int      SessionId,
    int      RequestId,
    int      SchedulerId,
    int      DegreeOfParallelism,
    DateTime RequestTime,
    DateTime? GrantTime,
    int      WaitForGrantMs,
    long     RequestedMemoryKB,
    long     GrantedMemoryKB,
    long     UsedMemoryKB,
    long     MaxUsedMemoryKB,
    double?  MemGrantUsedPct,
    long     IdealMemoryKB,
    long     RequiredMemoryKB,
    int?     QueueId,
    int?     WaitOrder,
    bool     IsNextCandidate,
    int      WaitTimeMs,
    string?  LoginName,
    string?  HostName,
    string?  ProgramName,
    string?  DatabaseName,
    string?  RequestStatus,
    string?  WaitType,
    string?  QueryText,
    DateTime CaptureTime,
    // Derived
    bool     IsPending,          // grant_time IS NULL
    string   Severity            // Critical / Warning / Info
);

/// <summary>A memory clerk entry showing what's consuming buffer pool.</summary>
public record MemoryClerk(
    string   ClerkType,
    string   ClerkName,
    long     TotalPagesKB,
    long     TotalPagesMB,
    double   PctOfTotalMemory,
    DateTime CaptureTime
);

/// <summary>Server-level memory performance counter.</summary>
public record MemoryCounter(
    string   CounterName,
    long     CounterValue,
    DateTime CaptureTime
);
