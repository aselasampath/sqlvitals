using System.Globalization;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Security;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Engine.Monitoring;

/// <summary>
/// The details drawer's rows (#149) for a row of Resource Queries, Query Regressions and SP Trace,
/// and (#150) File I/O, Agent Jobs, Config Checks, Security Audit, Index Health and Alerts:
/// what the grid has, labelled, with the units said.
/// </summary>
public static class RowDetails
{
    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // Microseconds, as the DMVs count them.
    private static string Us(long microseconds) => microseconds >= 1_000_000
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.##} s", microseconds / 1_000_000.0)
        : string.Format(CultureInfo.CurrentCulture, "{0:N1} ms", microseconds / 1_000.0);

    private static string Ms(double ms) => string.Format(CultureInfo.CurrentCulture, "{0:N1} ms", ms);

    private static string When(DateTime time) => time == default ? "—" : time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>A query from the plan cache, as Resource Queries lists it.</summary>
    public static IReadOnlyList<DetailRow> Rows(ResourceIntensiveQuery q)
    {
        var avgOf = q.ExecutionCount > 0 ? q.ExecutionCount : 1;
        return
        [
            new("Database", q.DatabaseName ?? "—"),
            new("Object", string.IsNullOrWhiteSpace(q.ObjectName) ? "Ad hoc" : q.ObjectName + (string.IsNullOrWhiteSpace(q.ObjectType) ? "" : $" ({q.ObjectType})")),
            new("Executions", N(q.ExecutionCount)),
            new("CPU", $"{Us(q.TotalCPUTime)} in all, {Us(q.AvgCPUTime)} a run"),
            new("Elapsed", $"{Us(q.TotalElapsedTime)} in all, {Us(q.AvgElapsedTime)} a run"),
            new("Logical reads", $"{N(q.TotalLogicalReads)} in all, {N(q.AvgLogicalReads)} a run"
                                 + (q.LastLogicalReads is { } last ? $", {N(last)} the last run" : "")),
            new("Physical reads", $"{N(q.TotalPhysicalReads)} in all, {N(q.TotalPhysicalReads / avgOf)} a run"),
            new("Writes", $"{N(q.TotalLogicalWrites)} in all"),
            new("Last run", When(q.LastExecutionTime)),
            new("Plan cached", When(q.PlanCreationTime)),
        ];
    }

    /// <summary>A regressed query: before and now side by side.</summary>
    public static IReadOnlyList<DetailRow> Rows(QueryRegression r)
    {
        static string Change(double? pct) => pct is { } p ? string.Format(CultureInfo.CurrentCulture, " ({0:+#,0;-#,0;0} %)", p) : "";
        return
        [
            new("Query", r.QueryStoreId is { } id ? $"Query Store query {id}" : $"Query hash {r.QueryKey}"),
            new("Database", r.DatabaseName ?? "—"),
            new("Slower in", r.RegressedOn, Warning: true),
            new("Duration", $"{Ms(r.BaselineAvgDurationMs)} before, {Ms(r.RecentAvgDurationMs)} now{Change(r.DurationChangePct)}", r.DurationRegressed),
            new("CPU", $"{Ms(r.BaselineAvgCpuMs)} before, {Ms(r.RecentAvgCpuMs)} now{Change(r.CpuChangePct)}", r.CpuRegressed),
            new("Runs", $"{N(r.BaselineExecutions)} before, {N(r.RecentExecutions)} now"),
            new("Extra time", string.Format(CultureInfo.CurrentCulture, "{0:N0} ms over the recent runs", r.ExtraTimeMs)),
            new("Plan", r.PlanChange.Length > 0
                ? $"{r.PlanChange}: {Plan(r.BaselinePlanId)} before, {Plan(r.RecentPlanId)} now"
                : r.RecentPlanId is null ? "The monitoring history keeps no plan ids" : $"The same plan, {Plan(r.RecentPlanId)}",
                r.PlanChange.Length > 0),
        ];
    }

    private static string Plan(long? id) => id is { } p ? $"plan {p}" : "no plan";

    /// <summary>One captured stored-procedure call.</summary>
    public static IReadOnlyList<DetailRow> Rows(SpTraceEvent call) =>
    [
        new("Time", call.EventTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture)),
        new("Procedure", string.IsNullOrWhiteSpace(call.ObjectName) ? "—" : call.ObjectName),
        new("Event", call.EventName),
        new("Database", call.DatabaseName ?? "—"),
        new("Duration", $"{N(call.DurationMs)} ms"),
        new("CPU", $"{N(call.CpuTimeMs)} ms"),
        new("Reads", $"{N(call.LogicalReads)} logical, {N(call.PhysicalReads)} physical"),
        new("Writes", N(call.Writes)),
        new("Rows", N(call.RowsReturned)),
        new("Session", call.SessionId.ToString(CultureInfo.CurrentCulture)),
        new("Login", call.LoginName ?? "—"),
        new("Application", call.ClientAppName ?? "—"),
        new("Host", call.ClientHostName ?? "—"),
    ];

    // ── #150: the Resources, Maintenance, Configuration and global pages ─────

    /// <summary>A database file's latency between the readings, as File I/O lists it.</summary>
    public static IReadOnlyList<DetailRow> Rows(FileLatency file) =>
    [
        new("Status", file.StatusText, file.IsSlow),
        new("Why", file.Explanation),
        new("Database", file.DatabaseName),
        new("File", $"{file.FileName} ({file.TypeText})"),
        new("Reads", file.Reads is null ? "—" : $"{file.ReadsText} at {Latency(file.ReadLatencyText)}, {file.AvgReadKBText} KB each", file.ReadIsSlow),
        new("Writes", file.Writes is null ? "—" : $"{file.WritesText} at {Latency(file.WriteLatencyText)}, {file.AvgWriteKBText} KB each", file.WriteIsSlow),
        new("Since startup", $"reads {Latency(file.StartupReadLatencyText)}, writes {Latency(file.StartupWriteLatencyText)}"),
        new("Size", file.SizeText.Length == 0 ? "—" : file.SizeText),
        new("Volume", file.Volume.Length == 0 ? "—" : file.Volume),
        new("Path", file.PhysicalName.Length == 0 ? "—" : file.PhysicalName),
    ];

    private static string Latency(string ms) => ms.Length == 0 ? "—" : ms + " ms";

    /// <summary>A SQL Agent job, as Agent Jobs lists it.</summary>
    public static IReadOnlyList<DetailRow> Rows(AgentJob job) =>
    [
        new("Status", job.StatusText, job.LastRunFailed || job.IsRunningLong),
        new("What happened", job.StatusDetail),
        new("Duration", job.Duration is null ? "—" : $"{job.DurationText}" + (job.AverageDuration is null ? "" : $", average {job.AverageText}")),
        new("Next run", job.NextRun is null ? job.NextRunText : $"{job.NextRunText} (server time)"),
        new("Failed runs", job.Runs == 0 ? "—" : $"{job.FailuresText} that msdb still keeps", job.FailedRuns > 0),
        new("Enabled", job.EnabledText),
        new("Category", job.Category ?? "—"),
        new("Owner", job.Owner ?? "(no such login): it can stop the job from starting", job.Owner is null),
        // msdb's own placeholder says nothing, so it isn't shown.
        new("Description", string.IsNullOrWhiteSpace(job.Description) || job.Description.Trim() == "No description available." ? "—" : job.Description.Trim()),
    ];

    /// <summary>A step of a job's run, or the run itself, as the message the drawer shows under the job.</summary>
    public static string Message(JobRun? run, JobStepRun? step) => step is not null
        ? $"-- Step {step.StepId}: {step.StepName} ({step.OutcomeText}" +
          (step.Start is { } start ? $", started {start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)}" : "") +
          $", took {step.DurationText})\n{step.Message}"
        : run?.Message ?? string.Empty;

    /// <summary>A configuration check: the value, what is recommended and why.</summary>
    public static IReadOnlyList<DetailRow> Rows(ConfigCheck check) =>
    [
        new("Status", check.StatusText, check.IsWarning),
        new("Scope", check.Scope),
        new("Current", check.Current.Length == 0 ? "—" : check.Current, check.IsWarning),
        new("Recommended", check.Recommended.Length == 0 ? "—" : check.Recommended),
        new("Why", check.Explanation),
    ];

    /// <summary>A finding of the security audit: what was found and its risk.</summary>
    public static IReadOnlyList<DetailRow> Rows(SecurityFinding finding) =>
    [
        new("Status", finding.StatusText, finding.Status is SecurityFindingStatus.Risk or SecurityFindingStatus.Review),
        new("Check", finding.Name),
        new("Scope", finding.Scope),
        new("Subject", finding.Subject.Length == 0 ? "—" : finding.Subject),
        new("Finding", finding.Finding),
        new("Risk", finding.Explanation),
    ];

    /// <summary>A missing index SQL Server suggests.</summary>
    public static IReadOnlyList<DetailRow> Rows(MissingIndex index) =>
    [
        new("Table", $"{index.DatabaseName}.{index.SchemaName}.{index.TableName}"),
        new("Severity", index.Severity, index.Severity.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase)),
        new("Impact", string.Format(CultureInfo.CurrentCulture, "{0:N1}: {1:N0} % cheaper, at a cost of {2:N2} a query", index.ImpactScore, index.AvgUserImpact, index.AvgTotalUserCost)),
        new("Would serve", $"{N(index.UserSeeks)} seeks, {N(index.UserScans)} scans, {N(index.UniqueCompiles)} compiles"),
        new("Last asked", When(new[] { index.LastUserSeek, index.LastUserScan }.Max() ?? default)),
        new("Equality", index.EqualityColumns ?? "—"),
        new("Inequality", index.InequalityColumns ?? "—"),
        new("Include", index.IncludedColumns ?? "—"),
    ];

    /// <summary>An alert, as the Alerts page lists it.</summary>
    public static IReadOnlyList<DetailRow> Rows(NamedAlert named, DateTime nowUtc)
    {
        var alert = named.Alert;
        return
        [
            new("Status", alert.IsActive ? "Active" : "Ended", alert.IsActive),
            new("Severity", AlertText.Severity(alert.Severity), alert.Severity == HealthLevel.Critical),
            new("Connection", named.Connection),
            new("Indicator", HealthThresholds.Info(alert.Indicator).Name),
            new("Started", When(alert.StartedUtc.ToLocalTime())),
            new("Ended", alert.EndedUtc is { } ended ? When(ended.ToLocalTime()) : "—"),
            new("Duration", AlertText.Duration(alert.Duration(nowUtc))),
            new("Worst value", AlertText.Value(alert.Indicator, alert.Value)),
            new("Threshold", AlertText.Threshold(alert.Indicator, alert.Threshold)),
            new("Ended because", alert.EndedUtc is null ? "—" : AlertText.EndReason(alert.EndReason)),
            new("Details", string.IsNullOrWhiteSpace(alert.Detail) ? "—" : alert.Detail),
        ];
    }
}
