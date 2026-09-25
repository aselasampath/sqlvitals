using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads deadlock reports from system_health, the session every SQL Server runs from the start
/// (#38). Nothing is created on the server: the session is already there, and only its
/// <c>xml_deadlock_report</c> events are read. The XML is shredded here, in
/// <see cref="DeadlockReportParser"/>.
/// </summary>
public class DeadlockRepository(IConfiguration configuration) : BaseRepository(configuration), IDeadlockRepository
{
    // Reading the event files goes through every rollover file system_health keeps.
    private const int FileReadTimeoutSec = 120;

    // VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE on SQL Server 2022) is missing.
    private static readonly int[] PermissionErrors = [297, 300];

    internal const string TargetsSql = """
        SELECT t.target_name AS TargetName,
               CASE WHEN t.target_name = N'event_file' THEN CAST(t.target_data AS NVARCHAR(MAX)) END AS TargetData
        FROM sys.dm_xe_sessions s
        JOIN sys.dm_xe_session_targets t ON t.event_session_address = s.address
        WHERE s.name = N'system_health'
        """;

    // The server filters by event name only; the rest of each event is read in C#.
    internal const string EventFileSql = """
        SELECT event_data AS EventData
        FROM sys.fn_xe_file_target_read_file(@pattern, NULL, NULL, NULL)
        WHERE object_name = N'xml_deadlock_report'
        """;

    internal const string RingBufferSql = """
        SELECT CAST(t.target_data AS NVARCHAR(MAX)) AS TargetData
        FROM sys.dm_xe_sessions s
        JOIN sys.dm_xe_session_targets t ON t.event_session_address = s.address
        WHERE s.name = N'system_health' AND t.target_name = N'ring_buffer'
        """;

    public async Task<DeadlockHistory> GetDeadlockHistoryAsync()
    {
        using var conn = CreateConnection();

        var edition = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition");
        if (edition is not null && Convert.ToInt32(edition.Edition) == 5)
            return DeadlockHistory.Unavailable(
                "Azure SQL Database has no system_health session to read. Deadlocks there are kept in the master " +
                "database's event log (sys.fn_xe_telemetry_blob_target_read_file); SQL Server and Azure SQL Managed " +
                "Instance are supported here.");

        List<dynamic> targets;
        try
        {
            targets = (await Q(conn, TargetsSql)).ToList();
        }
        catch (WaitStatsException ex) when (PermissionErrors.Contains(ex.SqlErrorNumber))
        {
            return DeadlockHistory.Unavailable(
                "This login can't read Extended Events sessions. Grant it VIEW SERVER STATE " +
                "(VIEW SERVER PERFORMANCE STATE on SQL Server 2022 and later) to see the deadlock history.");
        }

        if (targets.Count == 0)
            return DeadlockHistory.Unavailable(
                "The system_health Extended Events session isn't running on this server, so it has recorded no deadlocks. " +
                "Start it again with: ALTER EVENT SESSION system_health ON SERVER STATE = START;");

        var eventFile = targets.FirstOrDefault(t => (string)t.TargetName == "event_file");
        var hasRingBuffer = targets.Any(t => (string)t.TargetName == "ring_buffer");

        string? fileProblem = null;
        if (eventFile is not null)
        {
            var pattern = DeadlockReportParser.EventFilePattern(eventFile.TargetData as string);
            try
            {
                var rows = await Q(conn, EventFileSql, new { pattern }, FileReadTimeoutSec);
                var unreadable = 0;
                var reports = new List<DeadlockReport>();
                foreach (var row in rows)
                {
                    if (DeadlockReportParser.ParseEvent(row.EventData as string) is { } report) reports.Add(report);
                    else unreadable++;
                }
                return Build(reports, DeadlockSource.EventFile, unreadable, pattern, problem: null);
            }
            catch (WaitStatsException ex)
            {
                // The files can be unreadable where the DMVs aren't: a moved log folder, or a
                // managed instance. The ring buffer still has the latest deadlocks.
                fileProblem = $"The event files ({pattern}) could not be read: {ex.InnerException?.Message ?? ex.Message}";
                if (!hasRingBuffer) throw;
            }
        }

        if (!hasRingBuffer)
            return DeadlockHistory.Unavailable(
                "The system_health session on this server has neither its event file nor its ring buffer target, " +
                "so there is nowhere to read deadlocks from.");

        var ring = await QFirst(conn, RingBufferSql);
        var fromRing = DeadlockReportParser.ParseRingBuffer(ring?.TargetData as string, out int ringUnreadable);
        return Build(fromRing, DeadlockSource.RingBuffer, ringUnreadable, null,
                     fileProblem ?? "system_health has no event file target here.");
    }

    private static DeadlockHistory Build(IReadOnlyCollection<DeadlockReport> reports, DeadlockSource source,
                                         int unreadable, string? location, string? problem)
    {
        var newest = reports
            .OrderByDescending(r => r.TimestampUtc)
            .Take(DeadlockHistory.MaxDeadlocks)
            .ToList();
        return new DeadlockHistory(newest, source, reports.Count, unreadable, location, problem);
    }
}
