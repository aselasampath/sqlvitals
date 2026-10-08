using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Wait statistics: cumulative, active, signal versus resource, and the top wait types. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class WaitsRepository(IConfiguration configuration) : BaseRepository(configuration), IWaitsRepository
{
    // ── Helper: category → hex color ─────────────────────────────────
    internal static string CategoryColor(string cat) => cat switch
    {

        "CPU" => "#FF6B6B",
        "I/O" => "#FFA62B",
        "Memory" => "#7B68EE",
        "Locks" => "#FF4560",
        "Network" => "#00D9FF",
        "Benign/Idle" => "#555555",
        _ => "#26E7A6"
    };

    // ── 1. Cumulative wait stats ──────────────────────────────────────
    public async Task<IEnumerable<WaitStatCumulative>> GetCumulativeWaitsAsync()
    {
        var sql = SqlScripts.Get("Waits/CumulativeWaits");

        using var conn = CreateConnection();
        var rows = await Q(conn, sql);
        return rows.Select(r => new WaitStatCumulative(
            (string)r.WaitType, (string)r.WaitCategory, (int)r.IsBenign,
            (long)r.WaitingTasksCount, (long)r.WaitTimeMs, (long)r.MaxWaitTimeMs,
            (long)r.SignalWaitTimeMs, (long)r.ResourceWaitTimeMs,
            (double)(r.AvgWaitTimeMs ?? 0), (double)(r.SignalWaitPct ?? 0),
            (double)(r.PctOfTotalWaits ?? 0), (double)r.WaitTimeSec,
            (DateTime)r.CaptureTime, (DateTime)r.ServerStartTime,
            CategoryColor((string)r.WaitCategory),
            (double)(r.PctOfTotalWaits ?? 0) > 20 ? "High"
                : (double)(r.PctOfTotalWaits ?? 0) > 5 ? "Medium" : "Low"
        ));
    }

    // ── 2. Active waits ───────────────────────────────────────────────
    // sys.dm_exec_requests.dop is SQL Server 2016+; before that the degree of parallelism reads 0.
    internal static string ActiveWaitsSql(bool hasDop) => ActiveWaitsSqlTemplate.Replace("/*DOP*/", hasDop ? "r.dop" : "0");

    public async Task<IEnumerable<ActiveWait>> GetActiveWaitsAsync()
    {
        using var conn = CreateConnection();
        var sql = ActiveWaitsSql(await HasColumnAsync(conn, "sys.dm_exec_requests", "dop"));
        var rows = await Q(conn, sql);
        return rows.Select(r =>
        {
            double sec = (double)r.WaitTimeSec;
            string severity = sec > 60 ? "Critical" : sec > 10 ? "High" : sec > 1 ? "Medium" : "Low";
            return new ActiveWait(
                (int)r.SessionId, (int)r.BlockingSessionId,
                (string?)r.WaitType, (long)r.WaitTimeMs, sec,
                (string?)r.LastWaitType, (string?)r.WaitResource,
                (string?)r.RequestStatus, (string?)r.Command,
                (long)r.CpuTimeMs, (long)r.TotalElapsedMs,
                (long)r.LogicalReads, (long)r.Writes, (long)r.PhysicalReads,
                (long?)r.GrantedMemoryKB ?? 0, Convert.ToInt32(r.DegreeOfParallelism),
                (string?)r.LoginName, (string?)r.HostName,
                (string?)r.ProgramName, (string?)r.DatabaseName,
                (string)r.WaitCategory, (int)r.IsBlocked,
                (string?)r.QueryText, (DateTime)r.CaptureTime, severity
            );
        });
    }

    private static readonly string ActiveWaitsSqlTemplate = SqlScripts.Get("Waits/ActiveWaits");

    // ── 4. Signal vs resource ─────────────────────────────────────────
    public async Task<SignalVsResourceWait?> GetSignalVsResourceAsync()
    {
        var sql = SqlScripts.Get("Waits/SignalVsResource");

        using var conn = CreateConnection();
        var r = await QFirst(conn, sql);
        if (r is null) return null;
        return new SignalVsResourceWait(
            (long)r.TotalWaitMs, (long)r.TotalSignalMs, (long)r.TotalResourceMs,
            (double)(r.ServerSignalWaitPct ?? 0), (double)(r.ServerResourceWaitPct ?? 0),
            (string)r.CPUPressureLevel, (string)r.CPUPressureDescription,
            (double)r.TotalWaitSec, (double)r.TotalSignalSec, (double)r.TotalResourceSec,
            (DateTime)r.ServerStartTime, (DateTime)r.CaptureTime
        );
    }

    // ── 5. Top 25 wait types ──────────────────────────────────────────
    public async Task<IEnumerable<TopWaitType>> GetTopWaitTypesAsync()
    {
        var sql = SqlScripts.Get("Waits/TopWaitTypes");

        using var conn = CreateConnection();
        var rows = await Q(conn, sql);
        return rows.Select(r =>
        {
            string cat = (string)r.WaitCategory;
            string wt = (string)r.WaitType;
            int rank = (int)(long)r.WaitRank;
            return new TopWaitType(
                rank, wt, cat,
                (long)r.WaitingTasksCount, (long)r.WaitTimeMs, (long)r.MaxWaitTimeMs,
                (long)r.SignalWaitTimeMs, (long)r.ResourceWaitTimeMs,
                (double)(r.SignalWaitPct ?? 0), (double)r.WaitTimeSec,
                (double)(r.AvgWaitMsPerTask ?? 0), (double)(r.PctOfTotal ?? 0),
                (double)(r.CumulativePct ?? 0), (DateTime)r.CaptureTime,
                CategoryColor(cat),
                $"#{rank} {wt}"
            );
        });
    }
}
