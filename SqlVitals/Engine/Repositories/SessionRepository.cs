using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Repositories;

/// <summary>Sessions: the process map, a session's plan, and the applications connected. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class SessionRepository(IConfiguration configuration) : BaseRepository(configuration), ISessionRepository
{
    // ── 20. Process map (Active sessions + blocking) ─────────────────
    // A head blocker is often a sleeping session with an open transaction: it has no request, so
    // its text is the connection's most recent batch and its open transactions are the session's.
    // A blocker that isn't a user process (a system task) is included too, so its chain has a head.
    internal const string ProcessesSql = """
            SELECT s.session_id AS SessionId,
                r.blocking_session_id AS BlockingSessionId,
                s.login_name AS LoginName,
                s.host_name AS HostName,
                s.program_name AS ProgramName,
                ISNULL(r.status, s.status) AS Status,
                CASE WHEN s.session_id = @@SPID THEN 'Current' ELSE 'User' END AS Type,
                r.wait_type AS WaitType,
                r.last_wait_type AS LastWaitType,
                r.wait_resource AS WaitResource,
                r.wait_time AS WaitTimeMs,
                r.cpu_time AS CpuTimeMs,
                r.total_elapsed_time AS TotalElapsedMs,
                r.reads AS LogicalReads,
                ISNULL(r.reads,0) + ISNULL(r.writes,0) AS PhysicalIo,
                s.memory_usage AS MemoryUsage,
                s.login_time AS LoginTime,
                s.last_request_end_time AS LastBatch,
                ISNULL(r.open_transaction_count, s.open_transaction_count) AS OpenTran,
                r.command AS Command,
                DB_NAME(ISNULL(r.database_id, s.database_id)) AS DatabaseName,
                ISNULL(qt.text, '') AS QueryText,
                tx.TransactionBegin,
                SYSDATETIME() AS ServerNow
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
            -- When its oldest open transaction began, for the details drawer's transaction age (#147).
            OUTER APPLY (SELECT MIN(at.transaction_begin_time) AS TransactionBegin
                         FROM sys.dm_tran_session_transactions st
                         JOIN sys.dm_tran_active_transactions at ON at.transaction_id = st.transaction_id
                         WHERE st.session_id = s.session_id) tx
            OUTER APPLY (SELECT TOP (1) c.most_recent_sql_handle
                         FROM sys.dm_exec_connections c
                         WHERE c.session_id = s.session_id
                         ORDER BY c.last_read DESC) c
            OUTER APPLY sys.dm_exec_sql_text(ISNULL(r.sql_handle, c.most_recent_sql_handle)) qt
            WHERE s.is_user_process = 1
               OR s.session_id IN (SELECT blocking_session_id FROM sys.dm_exec_requests
                                   WHERE blocking_session_id > 0)
            """;

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
    internal const string SessionPlanSql = """
        SELECT TOP (1) CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan, x.IsRunning
        FROM (
            SELECT r.plan_handle, CAST(1 AS BIT) AS IsRunning, 0 AS Ord
            FROM sys.dm_exec_requests r
            WHERE r.session_id = @SessionId AND r.plan_handle IS NOT NULL
            UNION ALL
            SELECT last.plan_handle, CAST(0 AS BIT), 1
            FROM (SELECT TOP (1) qs.plan_handle
                  FROM sys.dm_exec_connections c
                  JOIN sys.dm_exec_query_stats qs ON qs.sql_handle = c.most_recent_sql_handle
                  WHERE c.session_id = @SessionId
                  ORDER BY qs.last_execution_time DESC) last
        ) x
        CROSS APPLY sys.dm_exec_query_plan(x.plan_handle) qp
        WHERE qp.query_plan IS NOT NULL
        ORDER BY x.Ord;
        """;

    public async Task<SessionPlan> GetSessionPlanAsync(int sessionId)
    {
        using var conn = CreateConnection();
        var row = await QFirst(conn, SessionPlanSql, new { SessionId = sessionId });
        return row is null ? new SessionPlan(null, false) : new SessionPlan((string?)row.QueryPlan, (bool)row.IsRunning);
    }

    // ── 21. Application connections ──────────────────────────────────
    public async Task<IEnumerable<ApplicationConnection>> GetApplicationConnectionsAsync()
    {
        const string sql = """
            SELECT
                ISNULL(s.program_name, '') AS ApplicationName,
                COUNT(*)                   AS CurrentConnections,
                SUM(CASE WHEN r.session_id IS NOT NULL THEN 1 ELSE 0 END) AS ActiveRequests,
                SUM(CAST(s.cpu_time AS BIGINT))         AS TotalCpuTimeMs,
                SUM(CAST(s.memory_usage AS BIGINT)) * 8 AS TotalMemoryKB,
                SUM(s.logical_reads)       AS TotalLogicalReads,
                SUM(s.reads)               AS TotalReads,
                SUM(s.writes)              AS TotalWrites,
                MIN(s.login_time)          AS FirstLoginTime,
                MAX(s.last_request_start_time) AS LastRequestTime,
                GETDATE()                  AS CaptureTime
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
            WHERE s.is_user_process = 1
            GROUP BY s.program_name
            ORDER BY COUNT(*) DESC
            """;

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
