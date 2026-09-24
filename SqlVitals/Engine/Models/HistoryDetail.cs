namespace SqlVitals.Engine.Models;

/// <summary>
/// Cumulative server counters read for a history detail snapshot. Two of these, taken a few
/// minutes apart, are turned into the per-interval changes that are stored (see
/// <see cref="SqlVitals.Engine.History.HistoryDetailTracker"/>).
/// </summary>
public sealed record HistoryDetailSnapshot(
    DateTime                       ServerTime,
    DateTime                       ServerStartTime,
    IReadOnlyList<WaitTypeTotals>  Waits,
    IReadOnlyList<FileIoTotals>    Files,
    IReadOnlyList<QueryTotals>     Queries,
    HistoryMemory?                 Memory,
    IReadOnlyList<CounterTotals>?  Counters = null);

/// <summary>
/// One of the Perfmon page's counters from sys.dm_os_performance_counters. A rate counter holds
/// a running total that has to be diffed; any other is a value as it stands.
/// </summary>
public sealed record CounterTotals(string CounterName, long Value, bool IsRate);

/// <summary>One row of sys.dm_os_wait_stats (benign waits excluded).</summary>
public sealed record WaitTypeTotals(string WaitType, long WaitingTasks, long WaitTimeMs, long SignalWaitTimeMs);

/// <summary>One row of sys.dm_io_virtual_file_stats.</summary>
public sealed record FileIoTotals(
    string DatabaseName, int FileId, string FileName, string FileType,
    long Reads, long BytesRead, long ReadStallMs,
    long Writes, long BytesWritten, long WriteStallMs);

/// <summary>sys.dm_exec_query_stats summed over every cached plan with the same query_hash.</summary>
public sealed record QueryTotals(
    string QueryHash, long Executions, long WorkerTimeUs, long ElapsedTimeUs,
    long LogicalReads, long LogicalWrites, DateTime FirstPlanCreated);

/// <summary>Memory Manager counters at the time of a detail snapshot (gauges, not deltas).</summary>
public sealed record HistoryMemory(
    long TotalServerMemoryKB, long TargetServerMemoryKB, long DatabaseCacheMemoryKB,
    long SqlCacheMemoryKB, long GrantedWorkspaceMemoryKB, long FreeMemoryKB,
    long MemoryGrantsOutstanding);

/// <summary>Statement text of one query_hash, taken from its most recently executed plan.</summary>
public sealed record QueryTextInfo(string QueryHash, string? DatabaseName, string QueryText);
