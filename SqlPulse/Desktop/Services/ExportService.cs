using System.IO;
using System.Text;
using SqlPulse.Engine.Models;
using SqlPulse.Engine.Repositories;

namespace SqlPulse.Desktop.Services;

/// <summary>
/// Fetches selected counter groups and writes a structured text report
/// suitable for pasting into an AI chat session.
/// </summary>
public class ExportService(IWaitStatsRepository repo)
{
    // ── Public counter group names (match checkboxes in ExportPage) ───
    public const string G_TOP_WAITS        = "Top Wait Types";
    public const string G_ACTIVE_WAITS     = "Active Waits (live)";
    public const string G_SIGNAL_VS_RES    = "Signal vs Resource Wait";
    public const string G_TEMPDB           = "TempDB Pressure";
    public const string G_MEMORY_GRANTS    = "Memory Grants";
    public const string G_QUERY_STORE      = "Query Store Health";
    public const string G_INDEX_HEALTH     = "Missing / Unused Indexes";
    public const string G_RESOURCE_QUERIES = "Resource-Intensive Queries";
    public const string G_INDEX_USAGE      = "Index Usage Patterns";
    public const string G_INDEX_FRAG       = "Index Fragmentation";
    public const string G_IMPLICIT_CONV    = "Implicit Conversions";
    public const string G_STALE_STATS      = "Stale Statistics";
    public const string G_DB_STORAGE       = "Database Storage & Configuration";

    public static readonly string[] AllGroups =
    [
        G_TOP_WAITS, G_ACTIVE_WAITS,
        G_SIGNAL_VS_RES, G_TEMPDB, G_MEMORY_GRANTS,
        G_QUERY_STORE, G_INDEX_HEALTH, G_RESOURCE_QUERIES, G_INDEX_USAGE,
        G_INDEX_FRAG, G_IMPLICIT_CONV, G_STALE_STATS, G_DB_STORAGE,
    ];

    // ── Entry point ────────────────────────────────────────────────────
    public async Task ExportAsync(IEnumerable<string> selectedGroups, string filePath,
        IProgress<string>? progress = null)
    {
        var groups = selectedGroups.ToHashSet();
        var sb = new StringBuilder();

        WriteHeader(sb);

        // Fire all selected queries concurrently
        var tasks = new List<(string Group, Task<string> Work)>();

        if (groups.Contains(G_TOP_WAITS))        tasks.Add((G_TOP_WAITS,        FetchTopWaits()));
        if (groups.Contains(G_ACTIVE_WAITS))     tasks.Add((G_ACTIVE_WAITS,     FetchActiveWaits()));
        if (groups.Contains(G_SIGNAL_VS_RES))    tasks.Add((G_SIGNAL_VS_RES,    FetchSignalVsResource()));
        if (groups.Contains(G_TEMPDB))           tasks.Add((G_TEMPDB,           FetchTempDb()));
        if (groups.Contains(G_MEMORY_GRANTS))    tasks.Add((G_MEMORY_GRANTS,    FetchMemoryGrants()));
        if (groups.Contains(G_QUERY_STORE))      tasks.Add((G_QUERY_STORE,      FetchQueryStore()));
        if (groups.Contains(G_INDEX_HEALTH))     tasks.Add((G_INDEX_HEALTH,     FetchIndexHealth()));
        if (groups.Contains(G_RESOURCE_QUERIES)) tasks.Add((G_RESOURCE_QUERIES, FetchResourceQueries()));
        if (groups.Contains(G_INDEX_USAGE))      tasks.Add((G_INDEX_USAGE,      FetchIndexUsage()));
        if (groups.Contains(G_INDEX_FRAG))       tasks.Add((G_INDEX_FRAG,       FetchIndexFrag()));
        if (groups.Contains(G_IMPLICIT_CONV))    tasks.Add((G_IMPLICIT_CONV,    FetchImplicitConv()));
        if (groups.Contains(G_STALE_STATS))      tasks.Add((G_STALE_STATS,      FetchStaleStats()));
        if (groups.Contains(G_DB_STORAGE))       tasks.Add((G_DB_STORAGE,       FetchDatabaseStorage()));

        await Task.WhenAll(tasks.Select(t => t.Work));

        foreach (var (group, task) in tasks)
        {
            progress?.Report($"Writing: {group}");
            sb.AppendLine(await task);
        }

        WriteFooter(sb);
        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
    }

    // ── Header / Footer ────────────────────────────────────────────────
    private static void WriteHeader(StringBuilder sb)
    {
        sb.AppendLine("=============================================================");
        sb.AppendLine("  SQL SERVER PERFORMANCE DIAGNOSTIC REPORT");
        sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("  Tool: SQL Database Monitor");
        sb.AppendLine("=============================================================");
        sb.AppendLine();
        sb.AppendLine("HOW TO USE THIS FILE WITH AI:");
        sb.AppendLine("  Paste the contents into ChatGPT, Copilot, or Claude and ask:");
        sb.AppendLine("  'Analyse this SQL Server diagnostic report and identify the");
        sb.AppendLine("   top performance issues with recommended actions.'");
        sb.AppendLine();
    }

    private static void WriteFooter(StringBuilder sb)
    {
        sb.AppendLine("=============================================================");
        sb.AppendLine("  END OF REPORT");
        sb.AppendLine("=============================================================");
    }

    // ── Section helpers ────────────────────────────────────────────────
    private static string Section(string title) =>
        $"\n{'─',61}\n  {title.ToUpperInvariant()}\n{'─',61}\n";

    private static string Kv(string key, object? value, string? note = null)
    {
        var line = $"  {key,-40} {value}";
        return note is not null ? $"{line}   ({note})" : line;
    }

    private static string Table<T>(IList<T> rows, params (string Header, Func<T, object?> Value)[] cols)
    {
        if (rows.Count == 0) return "  (no data)\n";

        var widths = cols.Select((c, i) =>
            Math.Max(c.Header.Length,
                rows.Take(200).Max(r => (c.Value(r)?.ToString() ?? "").Length))).ToArray();

        var sb = new StringBuilder();

        // Header
        for (int i = 0; i < cols.Length; i++)
            sb.Append("  " + cols[i].Header.PadRight(widths[i]) + "  ");
        sb.AppendLine();
        for (int i = 0; i < cols.Length; i++)
            sb.Append("  " + new string('-', widths[i]) + "  ");
        sb.AppendLine();

        // Rows (cap at 50 to keep file AI-friendly)
        foreach (var row in rows.Take(50))
        {
            for (int i = 0; i < cols.Length; i++)
                sb.Append("  " + (cols[i].Value(row)?.ToString() ?? "").PadRight(widths[i]) + "  ");
            sb.AppendLine();
        }
        if (rows.Count > 50) sb.AppendLine($"  ... ({rows.Count - 50} more rows truncated)");
        return sb.ToString();
    }

    // ── Fetch methods ──────────────────────────────────────────────────
    private async Task<string> FetchTopWaits()
    {
        var sb = new StringBuilder(Section(G_TOP_WAITS));
        try
        {
            var rows = (await repo.GetTopWaitTypesAsync()).ToList();
            sb.Append(Table(rows,
                ("Rank",        r => r.WaitRank),
                ("Wait Type",   r => r.WaitType),
                ("Category",    r => r.WaitCategory),
                ("Wait Sec",    r => r.WaitTimeSec.ToString("N1")),
                ("Avg ms/Task", r => r.AvgWaitMsPerTask.ToString("N2")),
                ("Signal %",    r => r.SignalWaitPct.ToString("N1")),
                ("% of Total",  r => r.PctOfTotal.ToString("N1")),
                ("Cum %",       r => r.CumulativePct.ToString("N1"))));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchActiveWaits()
    {
        var sb = new StringBuilder(Section(G_ACTIVE_WAITS));
        try
        {
            var rows = (await repo.GetActiveWaitsAsync()).ToList();
            if (rows.Count == 0) { sb.AppendLine("  (no active waits)"); return sb.ToString(); }
            sb.Append(Table(rows,
                ("Session", r => r.SessionId),
                ("Blocking", r => r.BlockingSessionId > 0 ? r.BlockingSessionId.ToString() : ""),
                ("Wait Type", r => r.WaitType),
                ("Wait Sec",  r => r.WaitTimeSec.ToString("N1")),
                ("Category",  r => r.WaitCategory),
                ("DB",        r => r.DatabaseName),
                ("Login",     r => r.LoginName),
                ("Query",     r => r.QueryText?.Substring(0, Math.Min(80, r.QueryText?.Length ?? 0)))));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchSignalVsResource()
    {
        var sb = new StringBuilder(Section(G_SIGNAL_VS_RES));
        try
        {
            var r = await repo.GetSignalVsResourceAsync();
            if (r is null) { sb.AppendLine("  (no data)"); return sb.ToString(); }
            sb.AppendLine(Kv("CPU Pressure Level",      r.CPUPressureLevel, r.CPUPressureLevel != "NORMAL" ? "ALERT" : null));
            sb.AppendLine(Kv("CPU Pressure Description", r.CPUPressureDescription));
            sb.AppendLine(Kv("Total Wait (sec)",         r.TotalWaitSec.ToString("N1")));
            sb.AppendLine(Kv("Signal Wait (CPU) %",      r.ServerSignalWaitPct.ToString("N1"), r.ServerSignalWaitPct > 25 ? "CRITICAL: >25%" : r.ServerSignalWaitPct > 10 ? "WARNING: >10%" : null));
            sb.AppendLine(Kv("Resource Wait (I/O) %",    r.ServerResourceWaitPct.ToString("N1")));
            sb.AppendLine(Kv("Signal Wait (sec)",        r.TotalSignalSec.ToString("N1")));
            sb.AppendLine(Kv("Resource Wait (sec)",      r.TotalResourceSec.ToString("N1")));
            sb.AppendLine(Kv("Server Start Time",        r.ServerStartTime.ToString("yyyy-MM-dd HH:mm")));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchTempDb()
    {
        var sb = new StringBuilder(Section(G_TEMPDB));
        try
        {
            var (files, sessions, counters) = await repo.GetTempDbPressureAsync();
            sb.AppendLine("  -- Counters --");
            foreach (var c in counters)
                sb.AppendLine(Kv(c.CounterName, c.CounterValue));
            sb.AppendLine();
            sb.AppendLine("  -- Files --");
            sb.Append(Table(files.ToList(),
                ("File",         f => (object?)f.FileName),
                ("Type",         f => (object?)f.FileType),
                ("Size MB",      f => (object?)f.FileSizeMB.ToString("N0")),
                ("Used MB",      f => (object?)f.SpaceUsedMB.ToString("N0")),
                ("Free MB",      f => (object?)f.FreeSpaceMB.ToString("N0")),
                ("% Used",       f => (object?)f.UsedPct.ToString("N1"))));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchMemoryGrants()
    {
        var sb = new StringBuilder(Section(G_MEMORY_GRANTS));
        try
        {
            var (grants, clerks, counters) = await repo.GetMemoryGrantsAsync();
            sb.AppendLine("  -- Counters --");
            foreach (var c in counters)
                sb.AppendLine(Kv(c.CounterName, c.CounterValue));
            sb.AppendLine();
            var gl = grants.ToList();
            if (gl.Count > 0)
            {
                sb.AppendLine("  -- Active Grants --");
                sb.Append(Table(gl,
                    ("Session",      g => (object?)g.SessionId),
                    ("DB",           g => (object?)g.DatabaseName),
                    ("Req KB",       g => (object?)g.RequestedMemoryKB),
                    ("Granted KB",   g => (object?)g.GrantedMemoryKB),
                    ("Used KB",      g => (object?)g.UsedMemoryKB),
                    ("Wait ms",      g => (object?)g.WaitTimeMs),
                    ("Status",       g => (object?)g.RequestStatus)));
            }
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchQueryStore()
    {
        var sb = new StringBuilder(Section(G_QUERY_STORE));
        try
        {
            var (health, topQueries) = await repo.GetQueryStoreAsync();
            if (health is not null)
            {
                sb.AppendLine(Kv("State",          health.ActualState));
                sb.AppendLine(Kv("Storage Used MB", health.CurrentStorageSizeMB));
                sb.AppendLine(Kv("Storage Max MB",  health.MaxStorageSizeMB));
                sb.AppendLine(Kv("Storage %",       health.StorageUsedPct.ToString("N1")));
                sb.AppendLine(Kv("Forced Plans",    health.ForcedPlans));
                sb.AppendLine();
            }
            var tq = topQueries.ToList();
            if (tq.Count > 0)
            {
                sb.AppendLine("  -- Top Queries --");
                sb.Append(Table(tq,
                    ("Query ID",     q => (object?)q.QueryId),
                    ("Executions",   q => (object?)q.TotalExecutions),
                    ("Total CPU ms", q => (object?)q.TotalCpuMs.ToString("N0")),
                    ("Avg CPU ms",   q => (object?)q.AvgCpuMs.ToString("N2")),
                    ("Forced Plan",  q => (object?)(q.IsForcedPlan ? "YES" : ""))));
            }
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchIndexHealth()
    {
        var sb = new StringBuilder(Section(G_INDEX_HEALTH));
        try
        {
            var (missing, unused) = await repo.GetIndexHealthAsync();
            var ml = missing.OrderByDescending(m => m.ImpactScore).ToList();
            if (ml.Count > 0)
            {
                sb.AppendLine("  -- Missing Indexes --");
                sb.Append(Table(ml,
                    ("Table",        m => m.TableName),
                    ("Impact Score", m => m.ImpactScore.ToString("N0")),
                    ("Seeks",        m => m.UserSeeks),
                    ("Severity",     m => m.Severity),
                    ("Eq Cols",      m => m.EqualityColumns),
                    ("Include Cols", m => m.IncludedColumns)));
            }
            var ul = unused.OrderByDescending(u => u.UserUpdates).ToList();
            if (ul.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  -- Unused Indexes (zero reads) --");
                sb.Append(Table(ul,
                    ("Table",     u => u.TableName),
                    ("Index",     u => u.IndexName),
                    ("Updates",   u => u.UserUpdates),
                    ("Seeks",     u => u.UserSeeks),
                    ("Scans",     u => u.UserScans)));
            }
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchResourceQueries()
    {
        var sb = new StringBuilder(Section(G_RESOURCE_QUERIES));
        try
        {
            var (byReads, byCpu, byHighestReads) = await repo.GetResourceIntensiveQueriesAsync();
            sb.AppendLine("  -- Top by Logical Reads --");
            var rr = byReads.ToList();
            sb.Append(Table(rr,
                ("DB",           r => r.DatabaseName),
                ("Object",       r => r.ObjectName),
                ("Exec Count",   r => r.ExecutionCount),
                ("Total Reads",  r => r.TotalLogicalReads),
                ("Avg Reads",    r => r.AvgLogicalReads),
                ("Avg CPU µs",   r => r.AvgCPUTime),
                ("Avg Elapsed µs", r => r.AvgElapsedTime),
                ("Last Exec",    r => r.LastExecutionTime.ToString("yyyy-MM-dd HH:mm")),
                ("Query",        r => r.QueryText?.Substring(0, Math.Min(60, r.QueryText?.Length ?? 0)))));

            sb.AppendLine();
            sb.AppendLine("  -- Top by CPU Time --");
            var cr = byCpu.ToList();
            sb.Append(Table(cr,
                ("DB",           r => r.DatabaseName),
                ("Object",       r => r.ObjectName),
                ("Exec Count",   r => r.ExecutionCount),
                ("Total CPU µs", r => r.TotalCPUTime),
                ("Avg CPU µs",   r => r.AvgCPUTime),
                ("Avg Reads",    r => r.AvgLogicalReads),
                ("Avg Elapsed µs", r => r.AvgElapsedTime),
                ("Query",        r => r.QueryText?.Substring(0, Math.Min(60, r.QueryText?.Length ?? 0)))));

            sb.AppendLine();
            sb.AppendLine("  -- Top by Highest Logical Reads (All Databases) --");
            var hr = byHighestReads.ToList();
            sb.Append(Table(hr,
                ("DB",           r => r.DatabaseName),
                ("Object",       r => r.ObjectName),
                ("Exec Count",   r => r.ExecutionCount),
                ("Total Reads",  r => r.TotalLogicalReads),
                ("Last Reads",   r => r.LastLogicalReads),
                ("Avg Reads",    r => r.AvgLogicalReads),
                ("Avg CPU µs",   r => r.AvgCPUTime),
                ("Avg Elapsed µs", r => r.AvgElapsedTime),
                ("Query",        r => r.QueryText?.Substring(0, Math.Min(60, r.QueryText?.Length ?? 0)))));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchIndexUsage()
    {
        var sb = new StringBuilder(Section(G_INDEX_USAGE));
        try
        {
            var rows = (await repo.GetIndexUsagePatternsAsync()).ToList();
            sb.Append(Table(rows,
                ("Table",    r => r.TableName),
                ("Index",    r => r.IndexName),
                ("Type",     r => r.IndexType),
                ("Seeks",    r => r.UserSeeks),
                ("Scans",    r => r.UserScans),
                ("Lookups",  r => r.UserLookups),
                ("Updates",  r => r.UserUpdates),
                ("Health",   r => r.IndexHealth)));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchIndexFrag()
    {
        var sb = new StringBuilder(Section(G_INDEX_FRAG));
        try
        {
            var rows = (await repo.GetIndexFragmentationAsync()).ToList();
            if (rows.Count == 0) { sb.AppendLine("  (no indexes require action)"); return sb.ToString(); }
            sb.Append(Table(rows,
                ("Table",     r => r.TableName),
                ("Index",     r => r.IndexName),
                ("Frag %",    r => r.FragmentationPercent.ToString("F1")),
                ("Pages",     r => r.PageCount),
                ("Density %", r => r.AvgPageSpaceUsed.ToString("F1")),
                ("Action",    r => r.RecommendedAction)));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchImplicitConv()
    {
        var sb = new StringBuilder(Section(G_IMPLICIT_CONV));
        try
        {
            var rows = (await repo.GetImplicitConversionsAsync()).ToList();
            if (rows.Count == 0) { sb.AppendLine("  (no implicit conversions found)"); return sb.ToString(); }
            sb.Append(Table(rows,
                ("DB",       r => r.DatabaseName),
                ("Object",   r => r.ObjectName),
                ("Exec",     r => r.ExecutionCount),
                ("Plan KB",  r => r.PlanSizeKB),
                ("Query",    r => r.QueryText?.Substring(0, Math.Min(80, r.QueryText?.Length ?? 0)))));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchStaleStats()
    {
        var sb = new StringBuilder(Section(G_STALE_STATS));
        try
        {
            var rows = (await repo.GetStaleStatisticsAsync()).ToList();
            if (rows.Count == 0) { sb.AppendLine("  (no stale statistics)"); return sb.ToString(); }
            sb.Append(Table(rows,
                ("Table",         r => r.TableName),
                ("Statistics",    r => r.StatisticsName),
                ("Last Updated",  r => r.LastUpdated?.ToString("yyyy-MM-dd") ?? "Never"),
                ("Days Old",      r => r.DaysOld),
                ("Rows",          r => r.RowsInTable),
                ("Modifications", r => r.ModificationsSinceLastUpdate),
                ("Sample %",      r => r.SamplePct.ToString("F1"))));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    private async Task<string> FetchDatabaseStorage()
    {
        var sb = new StringBuilder(Section(G_DB_STORAGE));
        try
        {
            var (config, files, tempFiles, tables) = await repo.GetDatabaseStorageAsync();

            // ── Server configuration ──────────────────────────────────
            sb.AppendLine("  -- Server Configuration --");
            foreach (var c in config.OrderBy(x => x.ParameterName))
                sb.AppendLine(Kv(c.ParameterName, c.CurrentValue));
            sb.AppendLine();

            // ── Database file summary (aggregate per DB) ──────────────
            sb.AppendLine("  -- Database File Summary --");
            var dbGroups = files.GroupBy(f => f.DatabaseName).OrderBy(g => g.Key);
            var dbSummary = dbGroups.Select(g => new
            {
                Database   = g.Key,
                Recovery   = g.First().RecoveryModel,
                Compat     = g.First().CompatibilityLevel,
                DataMB     = g.Where(f => f.FileType == "ROWS").Sum(f => f.FileSizeMB),
                LogMB      = g.Where(f => f.FileType == "LOG") .Sum(f => f.FileSizeMB),
                OtherMB    = g.Where(f => f.FileType != "ROWS" && f.FileType != "LOG").Sum(f => f.FileSizeMB),
                TotalMB    = g.Sum(f => f.FileSizeMB),
                QS         = g.First().QueryStoreOn ? "Yes" : "No",
                Encrypted  = g.First().Encrypted    ? "Yes" : "No",
            }).ToList();
            sb.Append(Table(dbSummary,
                ("Database",   r => (object?)r.Database),
                ("Recovery",   r => (object?)r.Recovery),
                ("Compat",     r => (object?)r.Compat),
                ("Data MB",    r => (object?)r.DataMB.ToString("N2")),
                ("Log MB",     r => (object?)r.LogMB.ToString("N2")),
                ("Other MB",   r => (object?)r.OtherMB.ToString("N2")),
                ("Total MB",   r => (object?)r.TotalMB.ToString("N2")),
                ("QS On",      r => (object?)r.QS),
                ("Encrypted",  r => (object?)r.Encrypted)));
            sb.AppendLine();

            // ── TempDB file summary ───────────────────────────────────
            sb.AppendLine("  -- TempDB Files --");
            var tempList = tempFiles.ToList();
            sb.Append(Table(tempList,
                ("File",     t => (object?)t.FileName),
                ("Type",     t => (object?)t.FileType),
                ("Size MB",  t => (object?)t.SizeMB.ToString("N2")),
                ("Used MB",  t => (object?)t.UsedMB.ToString("N2")),
                ("Free MB",  t => (object?)t.FreeMB.ToString("N2"))));
            sb.AppendLine();

            // ── Top tables ────────────────────────────────────────────
            sb.AppendLine("  -- Top 20 Tables by Storage --");
            var tbl = tables.ToList();
            sb.Append(Table(tbl,
                ("Schema",    r => (object?)r.SchemaName),
                ("Table",     r => (object?)r.TableName),
                ("Rows",      r => (object?)r.RowCount.ToString("N0")),
                ("Data MB",   r => (object?)r.DataSizeMB.ToString("N3")),
                ("Index MB",  r => (object?)r.IndexSizeMB.ToString("N3")),
                ("Total MB",  r => (object?)r.TotalSizeMB.ToString("N3")),
                ("Indexes",   r => (object?)r.IndexCount),
                ("Type",      r => (object?)r.TableType)));
        }
        catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
        return sb.ToString();
    }
}
