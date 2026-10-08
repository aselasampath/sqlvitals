using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Repositories;

/// <summary>Sessions: the process map, a session's plan, and the applications connected. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class SessionRepository(SqlTarget target) : BaseRepository(target), ISessionRepository
{
    // ── 20. Process map (Active sessions + blocking) ─────────────────
    // A head blocker is often a sleeping session with an open transaction: it has no request, so
    // its text is the connection's most recent batch and its open transactions are the session's.
    // A blocker that isn't a user process (a system task) is included too, so its chain has a head.
    internal static readonly string ProcessesSql = SqlScripts.Get("Session/Processes");

    public async Task<IEnumerable<ProcessNode>> GetProcessesAsync()
    {
        using var conn = CreateConnection();
        var rows = await Q(conn, ProcessesSql);
        return BlockingChains.Classify(rows.Select(r => new ProcessNode(
            (int)r.SessionId,
            (int)(r.BlockingSessionId ?? 0),
            (string)r.Status,
            (string?)r.LoginName,
            (string?)r.HostName,
            (string?)r.ProgramName,
            (string?)r.WaitType,
            (long)(r.WaitTimeMs ?? 0),
            (string?)r.LastWaitType,
            (string?)r.WaitResource,
            (string?)r.DatabaseName,
            (long)(r.CpuTimeMs ?? 0),
            (long)(r.PhysicalIo ?? 0),
            (long)(r.MemoryUsage ?? 0),
            (DateTime)(r.LoginTime ?? DateTime.MinValue),
            (DateTime)(r.LastBatch ?? DateTime.MinValue),
            (int)(r.OpenTran ?? 0),
            (string?)r.Command,
            (string?)r.QueryText,
            "",  // set by Classify
            (DateTime?)r.TransactionBegin,
            (DateTime?)r.ServerNow
        )));
    }

    // The plan of what a session is running; for a session between requests (a sleeping head
    // blocker), the cached plan of its last statement, the newest one when there are several (#147).
    internal static readonly string SessionPlanSql = SqlScripts.Get("Session/SessionPlan");

    public async Task<SessionPlan> GetSessionPlanAsync(int sessionId)
    {
        using var conn = CreateConnection();
        var row = await QFirst(conn, SessionPlanSql, new { SessionId = sessionId });
        return row is null ? new SessionPlan(null, false) : new SessionPlan((string?)row.QueryPlan, (bool)row.IsRunning);
    }

    // ── 21. Application connections ──────────────────────────────────
    public async Task<IEnumerable<ApplicationConnection>> GetApplicationConnectionsAsync()
    {
        var sql = SqlScripts.Get("Session/ApplicationConnections");

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r =>
            new ApplicationConnection(
                (string)(r.ApplicationName ?? ""),
                (int)r.CurrentConnections,
                (int)r.ActiveRequests,
                (long)r.TotalCpuTimeMs,
                (long)r.TotalMemoryKB,
                (long)r.TotalLogicalReads,
                (long)r.TotalReads,
                (long)r.TotalWrites,
                (DateTime)r.FirstLoginTime,
                (DateTime)r.LastRequestTime,
                (DateTime)r.CaptureTime));
    }
}
