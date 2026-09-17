using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlPulse.Engine.Models;
using SqlPulse.Engine.Repositories;

namespace SqlPulse.Desktop.Pages;

public partial class OverviewPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public OverviewPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        var kpiTask  = _repo.GetServerHealthKpiAsync();
        var utilTask = _repo.GetUtilizationKpiAsync();

        await System.Threading.Tasks.Task.WhenAll(kpiTask, utilTask);

        var kpi  = await kpiTask;
        var util = await utilTask;

        // ── KPI cards ──────────────────────────────────────────────────
        KpiGrid.Children.Clear();
        if (kpi is not null)
        {
            AddKpiCard("CPU Pressure",      kpi.CPUPressureStatus,                   kpi.CPUStatusColor,
                "Derived from sys.dm_os_ring_buffers. Indicates whether the OS is signalling SQL Server about memory pressure on the CPU scheduler. 'Normal' = healthy; 'Warning/Critical' = investigate runaway queries or missing indexes.");
            AddKpiCard("Memory Use",        $"{kpi.MemoryUtilizationPct:F1}%",       kpi.MemoryUtilizationPct > 85 ? "#EF4444" : "#22C55E",
                "% of total physical RAM currently consumed by the SQL Server process (from dm_os_process_memory). Above 85 % is a warning — the OS may start reclaiming pages from the buffer pool.");
            AddKpiCard("PLE Status",        kpi.PLEStatus,                           kpi.PLEStatus.Contains("Critical") ? "#EF4444" : kpi.PLEStatus.Contains("WARNING") ? "#FBBF24" : "#22C55E",
                "Page Life Expectancy — how long (seconds) a data page stays in the buffer pool before being evicted. Rule-of-thumb: PLE < 300 s on older systems, or < (RAM_GB / 4) × 300 on modern servers, indicates memory pressure.");
            AddKpiCard("Active Sessions",   kpi.ActiveUserSessions.ToString(),       "#7C3AED",
                "Number of currently connected user sessions (from sys.dm_exec_sessions). Sudden spikes may indicate connection pool leaks or an application storm.");
            AddKpiCard("Blocked Requests",  kpi.BlockedRequests.ToString(),          kpi.BlockedRequests > 0 ? "#EF4444" : "#22C55E",
                "Requests currently blocked waiting for a lock (blocking_session_id IS NOT NULL in dm_exec_requests). Any value > 0 warrants investigation — chronic blocking degrades throughput and increases deadlock risk.");
            AddKpiCard("Uptime (days)",     kpi.UptimeDays.ToString(),              "#94A3B8",
                "Days since the SQL Server service last started (sqlserver_start_time in sys.dm_os_sys_info). Very short uptime may indicate recent restarts; very long uptime means statistics and cached plans are fully warmed up.");
            AddKpiCard("Buffer Cache Hit",  $"{kpi.BufferCacheHitRatio:F1}%",       kpi.BufferCacheHitRatio < 95 ? "#FBBF24" : "#22C55E",
                "% of page requests satisfied from the buffer pool without a physical disk read (Buffer cache hit ratio in dm_os_performance_counters). Should stay above 95 %. Values below 90 % indicate insufficient RAM.");
            AddKpiCard("Batch Req/sec",     kpi.BatchRequestsPerSec.ToString(),      "#7C3AED",
                "Number of T-SQL command batches received per second (Batch Requests/sec). Use as a throughput baseline — compare over time to detect load increases or application storms.");
            AddKpiCard("SQL Compilations/s",  kpi.SQLCompilationsPerSec.ToString(),  "#7C3AED",
                "Number of query plan compilations per second. High values relative to Batch Req/sec (> 10 %) suggest a plan cache churn problem — caused by ad-hoc queries, RECOMPILE hints, or excessive parameter sniffing.");
            AddKpiCard("Recompilations/s",    kpi.SQLRecompilationsPerSec.ToString(),"#FFA62B",
                "Number of times a cached plan was discarded and recompiled mid-execution per second. Persistent recompiles waste CPU and inflate Compilations/sec. Common causes: schema changes, SET option changes, table variable DML.");
            AddKpiCard("Lock Waits/s",        kpi.LockWaitsPerSec.ToString(),        kpi.LockWaitsPerSec > 0 ? "#EF4444" : "#22C55E",
                "Number of lock requests that had to wait per second (Lock Waits/sec). A rising trend means increasing contention — look for missing indexes, long-running transactions, or poor isolation level choices.");
            AddKpiCard("Deadlocks/s",         kpi.DeadlocksPerSec.ToString(),        kpi.DeadlocksPerSec > 0 ? "#FF4560" : "#22C55E",
                "Number of deadlocks per second (Number of Deadlocks/sec). Any value > 0 is a red flag. Deadlocks force one transaction to be killed as a victim — check deadlock graphs via Extended Events or the system health session.");
            AddKpiCard("Transactions/s",      kpi.TransactionsPerSec.ToString(),     "#94A3B8",
                "Total user transactions committed or rolled back per second. Use alongside Batch Req/sec to gauge transaction density. A rising gap between the two can indicate batches with multiple statements per transaction.");
            AddKpiCard("Mem Grants Pending",  kpi.MemoryGrantsPending.ToString(),    kpi.MemoryGrantsPending > 0 ? "#EF4444" : "#22C55E",
                "Number of queries currently waiting for a workspace memory grant to execute (Memory Grants Pending). Any value > 0 causes query queuing and latency spikes. Root cause is usually large sort/hash operations or insufficient max server memory.");
            AddKpiCard("Total Server Mem",    $"{kpi.TotalServerMemKB / 1024.0 / 1024.0:N1} GB", "#7C3AED",
                "Total Server Memory (KB) — the amount of memory SQL Server is currently using from the buffer pool (dm_os_performance_counters). Compare with Target Server Memory to see how close SQL is to its memory limit.");
            AddKpiCard("Target Server Mem",   $"{kpi.TargetServerMemKB / 1024.0 / 1024.0:N1} GB", "#94A3B8",
                "Target Server Memory (KB) — the maximum amount of memory SQL Server is configured to use (max server memory setting). If Total ≈ Target, the buffer pool is fully committed. A large gap means SQL has not needed that much RAM yet.");
        }

        // ── Utilization panels ─────────────────────────────────────────
        if (util is not null)
            ApplyUtilization(util);
    }

    private void ApplyUtilization(UtilizationKpi u)
    {
        // ── CPU progress bars ─────────────────────────────────────────
        SetBar(PbAvgCpu, TxtAvgCpu, u.AvgCpuPct);
        SetBar(PbP95Cpu, TxtP95Cpu, u.P95CpuPct);
        SetBar(PbMaxCpu, TxtMaxCpu, u.MaxCpuPct);

        // Workers info line
        string workers = u.LogicalCpus > 0
            ? $"{u.LogicalCpus} CPUs,  {u.WorkersUsed:N0} / {u.WorkersTotal:N0} workers,  {u.SampleCount:N0} samples"
            : $"{u.SampleCount:N0} samples";
        TxtCpuInfo.Text = workers;

        // ── Memory progress bars ──────────────────────────────────────
        double stolenPct = u.TotalServerMemKB > 0
            ? Math.Round((double)u.StolenMemKB / u.TotalServerMemKB * 100, 1) : 0;
        double bufferPct = u.TotalServerMemKB > 0
            ? Math.Round((double)u.DatabaseCacheMemKB / u.TotalServerMemKB * 100, 1) : 0;

        SetBar(PbStolen, TxtStolen, stolenPct);
        SetBar(PbBuffer, TxtBuffer, bufferPct);

        // Memory KPI text
        TxtMemPhysical.Text = FormatMB(u.PhysicalMemoryKB / 1024);
        TxtMemTarget.Text   = FormatMB(u.TargetMemKB      / 1024);
        TxtMemTotal.Text    = FormatMB(u.TotalServerMemKB / 1024);
        TxtMemBuffer.Text   = FormatMB(u.DatabaseCacheMemKB / 1024);

        // ── Health badge ──────────────────────────────────────────────
        string label;
        Color  bg;
        if (u.P95CpuPct >= 85)
        {
            label = "UNDER PROVISIONED";
            bg    = Color.FromRgb(239, 68, 68);
        }
        else if (u.P95CpuPct >= 60 || u.MaxCpuPct >= 90)
        {
            label = "UNDER PRESSURE";
            bg    = Color.FromRgb(249, 115, 22);
        }
        else
        {
            label = "WELL PROVISIONED";
            bg    = Color.FromRgb(22, 163, 74);
        }
        TxtHealthBadge.Text    = label;
        BadgeBorder.Background = new SolidColorBrush(bg);
    }

    // ── helpers ───────────────────────────────────────────────────────
    private static void SetBar(ProgressBar pb, TextBlock txt, double pct)
    {
        double clamped = Math.Clamp(pct, 0, 100);
        pb.Value       = clamped;
        pb.Foreground  = PctBrush(clamped);
        txt.Text       = $"{pct:N1}%";
        txt.Foreground = PctBrush(clamped);
    }

    private static SolidColorBrush PctBrush(double pct) =>
        pct >= 85 ? new SolidColorBrush(Color.FromRgb(239, 68,  68)) :
        pct >= 50 ? new SolidColorBrush(Color.FromRgb(251, 146, 60)) :
                    new SolidColorBrush(Color.FromRgb(34,  197, 94));

    private static string FormatMB(long mb) =>
        mb >= 1024 ? $"{mb / 1024.0:N1} GB" : $"{mb:N0} MB";

    private void AddKpiCard(string label, string value, string hexColor, string? tooltip = null)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
        var card = new Border
        {
            Style  = (Style)Application.Current.FindResource("KpiCard"),
            Margin = new Thickness(0, 0, 10, 10),
            Width  = 200,
        };
        if (tooltip is not null)
            ToolTipService.SetToolTip(card, MakeTooltip(label, tooltip));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.FindResource("KpiValue"), Foreground = brush });
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("KpiLabel") });
        card.Child = sp;
        KpiGrid.Children.Add(card);
    }

    private static ToolTip MakeTooltip(string title, string description)
    {
        var sp = new StackPanel { MaxWidth = 340 };
        sp.Children.Add(new TextBlock { Text = title,       FontWeight = FontWeights.Bold,   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,4) });
        sp.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        return new ToolTip { Content = sp };
    }
}
