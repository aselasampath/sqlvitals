using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.History;

/// <summary>
/// The group a wait type is in, as <c>Sql/Waits/TopWaitTypes.sql</c> puts it: CPU, I/O, Memory,
/// Locks, Network or Other. For waits read from the monitoring history, which keeps the wait type
/// but not its group.
/// </summary>
public static class WaitCategory
{
    private static readonly HashSet<string> Cpu = new(StringComparer.OrdinalIgnoreCase)
    {
        "SOS_SCHEDULER_YIELD", "THREADPOOL", "CMEMTHREAD", "CMEMPARTITIONED", "EE_PMOLOCK", "EXCHANGE",
        "CXPACKET", "CXCONSUMER", "CXSYNC_PORT", "CXSYNC_CONSUMER",
    };

    private static readonly HashSet<string> Io = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASYNC_IO_COMPLETION", "IO_COMPLETION", "PAGEIOLATCH_SH", "PAGEIOLATCH_EX", "PAGEIOLATCH_UP", "PAGEIOLATCH_NL",
        "PAGEIOLATCH_DT", "PAGEIOLATCH_KP", "WRITELOG", "WRITE_COMPLETION", "IO_QUEUE_LIMIT", "IO_RETRY", "BACKUPIO",
        "BACKUPBUFFER", "BACKUPTHREAD", "LOGMGR", "LOGMGR_QUEUE", "LOGMGR_FLUSH", "LOGMGR_RESERVE_APPEND", "LOG_RATE_GOVERNOR",
    };

    private static readonly HashSet<string> Memory = new(StringComparer.OrdinalIgnoreCase)
    {
        "RESOURCE_SEMAPHORE", "RESOURCE_SEMAPHORE_QUERY_COMPILE", "PAGELATCH_SH", "PAGELATCH_EX", "PAGELATCH_UP",
        "PAGELATCH_NL", "PAGELATCH_DT", "PAGELATCH_KP", "LATCH_SH", "LATCH_EX", "LATCH_NL", "LATCH_DT", "LATCH_KP",
        "LATCH_UP", "MEMORY_ALLOCATION_EXT", "SOS_VIRTUALMEMORY_LOW",
    };

    private static readonly HashSet<string> Network = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASYNC_NETWORK_IO", "NET_WAITFOR_PACKET", "NETWORK_IO", "OLEDB",
    };

    public static string Of(string waitType) =>
        Cpu.Contains(waitType)     ? "CPU"
        : Io.Contains(waitType)    ? "I/O"
        : Memory.Contains(waitType) ? "Memory"
        : waitType.StartsWith("LCK_M", StringComparison.OrdinalIgnoreCase) || waitType.Equals("LOCK_HASH", StringComparison.OrdinalIgnoreCase) ? "Locks"
        : Network.Contains(waitType) ? "Network"
        : "Other";
}

/// <summary>
/// The top wait types over a past range (#178): the Waits page's table for a window picked from
/// the monitoring history, ranked and shared out as the server's own list is since it started.
/// The history leaves out the benign waits already.
/// </summary>
public static class HistoryTopWaits
{
    /// <summary>As many as the server's list has: <c>TOP 25</c>.</summary>
    public const int Count = 25;

    /// <param name="buckets">The range's waits, bucket by bucket.</param>
    /// <param name="capturedAt">The range's end, on this PC's clock: the rows' capture time.</param>
    public static IReadOnlyList<TopWaitType> Over(IEnumerable<WaitHistoryBucket> buckets, DateTime capturedAt)
    {
        var totals = buckets
            .SelectMany(b => b.Waits.Values)
            .GroupBy(w => w.WaitType, StringComparer.OrdinalIgnoreCase)
            .Select(g => new WaitTypeTotals(g.Key, g.Sum(w => w.WaitingTasks), g.Sum(w => w.WaitTimeMs), g.Sum(w => w.SignalWaitTimeMs)))
            .Where(w => w.WaitTimeMs > 0)
            .OrderByDescending(w => w.WaitTimeMs).ThenBy(w => w.WaitType, StringComparer.Ordinal)
            .ToList();

        var all = (double)totals.Sum(w => w.WaitTimeMs);
        var cumulative = 0d;
        return totals.Take(Count).Select((w, i) =>
        {
            var share   = all > 0 ? w.WaitTimeMs / all * 100 : 0;
            cumulative += share;
            var category = WaitCategory.Of(w.WaitType);
            var rank     = i + 1;
            return new TopWaitType(
                rank, w.WaitType, category, w.WaitingTasks, w.WaitTimeMs,
                MaxWaitTimeMs:      0,   // the history keeps totals only
                w.SignalWaitTimeMs, w.WaitTimeMs - w.SignalWaitTimeMs,
                SignalWaitPct:      w.WaitTimeMs > 0 ? (double)w.SignalWaitTimeMs / w.WaitTimeMs * 100 : 0,
                WaitTimeSec:        w.WaitTimeMs / 1000.0,
                AvgWaitMsPerTask:   w.WaitingTasks > 0 ? (double)w.WaitTimeMs / w.WaitingTasks : 0,
                PctOfTotal:         share,
                CumulativePct:      cumulative,
                CaptureTime:        capturedAt,
                CategoryColor:      WaitsRepository.CategoryColor(category),
                RankLabel:          $"#{rank} {w.WaitType}");
        }).ToList();
    }
}
