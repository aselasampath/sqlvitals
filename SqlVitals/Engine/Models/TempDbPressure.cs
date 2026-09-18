namespace SqlVitals.Engine.Models;

/// <summary>TempDB file space usage (one row per file).</summary>
public record TempDbFile(
    string FileName,
    string FileType,
    string PhysicalPath,
    long   FileSizeMB,
    long   SpaceUsedMB,
    long   FreeSpaceMB,
    double UsedPct,
    string AutoGrowth,
    DateTime CaptureTime
);

/// <summary>A single session consuming TempDB space (spills, sorts, user objects).</summary>
public record TempDbSession(
    int     SessionId,
    long    UserObjAllocKB,
    long    UserObjDeallocKB,
    long    InternalObjAllocKB,   // sort/hash/build spills
    long    InternalObjDeallocKB,
    long    NetTempDBUsageKB,
    string? LoginName,
    string? HostName,
    string? ProgramName,
    string? WaitType,
    string? RequestStatus,
    long    CpuTimeMs,
    string? DatabaseName,
    string? QueryText,
    DateTime CaptureTime,
    // Derived
    string  Severity              // Critical / Warning / Info
);

/// <summary>Server-level TempDB performance counter.</summary>
public record TempDbCounter(
    string   CounterName,
    long     CounterValue,
    DateTime CaptureTime
);
