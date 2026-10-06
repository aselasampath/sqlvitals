using System.Globalization;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Verdicts;

/// <summary>
/// Index Health's verdict (#150): the missing indexes SQL Server asks for, the most valuable
/// first, and the unused ones that only cost writes.
/// </summary>
public static class IndexHealthVerdict
{
    /// <summary>Opens the Missing Indexes tab with the most valuable selected.</summary>
    public const string ShowMissing = "show-missing";

    /// <summary>Opens the Unused Indexes tab.</summary>
    public const string ShowUnused = "show-unused";

    /// <summary>The suggestion the verdict names: critical first, then the highest impact.</summary>
    public static MissingIndex? Top(IEnumerable<MissingIndex> missing) => missing
        .OrderBy(m => Rank(m.Severity)).ThenByDescending(m => m.ImpactScore).FirstOrDefault();

    private static int Rank(string severity) => severity.ToUpperInvariant() switch
    {
        "CRITICAL" => 0,
        "WARNING"  => 1,
        _          => 2,
    };

    public static PageVerdict From(IReadOnlyList<MissingIndex> missing, IReadOnlyList<UnusedIndex> unused)
    {
        var culture = CultureInfo.CurrentCulture;
        var unusedText = unused.Count == 0 ? "No index is maintained without being read."
            : string.Format(culture, "{0} only cost writes: not read since the server started, updated {1:N0} times.",
                            Sizes.Plural(unused.Count, "unused index", "unused indexes"), unused.Sum(u => u.UserUpdates));
        List<VerdictAction> unusedAction = unused.Count == 0 ? [] : [new(ShowUnused, "Unused indexes")];

        if (Top(missing) is not { } top)
            return new PageVerdict(unused.Count == 0 ? VerdictLevel.Ok : VerdictLevel.Info, "SQL Server suggests no missing index",
                unusedText + " Both lists start again when SQL Server restarts.", unusedAction);

        var critical = missing.Count(m => Rank(m.Severity) == 0);
        var headline = string.Format(culture, "{0} suggested{1}; the most valuable on {2}.{3}.{4}",
                                     Sizes.Plural(missing.Count, "missing index", "missing indexes"),
                                     critical > 0 ? string.Format(culture, ", {0:N0} critical", critical) : "",
                                     top.DatabaseName, top.SchemaName, top.TableName);
        var keys = string.Join(", ", new[] { top.EqualityColumns, top.InequalityColumns }.Where(c => !string.IsNullOrWhiteSpace(c)));
        var why = string.Format(culture, "It would serve {0:N0} seeks and {1:N0} scans (impact {2:N0}), on {3}. Check it against the table's existing indexes before creating it: every index slows writes. ",
                                top.UserSeeks, top.UserScans, top.ImpactScore, keys.Length == 0 ? "the columns listed" : keys)
                + unusedText;
        return new PageVerdict(critical > 0 ? VerdictLevel.Warning : VerdictLevel.Info, headline, why,
                               [new(ShowMissing, "Show it"), .. unusedAction]);
    }
}

/// <summary>Stale Stats' verdict (#150): the statistics with the largest share of their table changed since they were updated.</summary>
public static class StaleStatsVerdict
{
    /// <summary>Selects the most changed statistic.</summary>
    public const string ShowWorst = "show-worst";

    /// <summary>Changes from which a statistic is out of date: SQL Server's own auto-update threshold on small tables.</summary>
    public const double StalePct = 20;

    /// <summary>The share of the table's rows changed since the statistic was updated; null when the table is empty or unknown.</summary>
    public static double? ChangedPct(StaleStatistic s) =>
        s.RowsInTable is { } rows and > 0 ? 100.0 * s.ModificationsSinceLastUpdate / rows : null;

    /// <summary>The statistic the verdict names: the largest share changed, then the most changes.</summary>
    public static StaleStatistic? Worst(IEnumerable<StaleStatistic> stats) => stats
        .Where(s => s.ModificationsSinceLastUpdate > 0)
        .OrderByDescending(s => ChangedPct(s) ?? 0).ThenByDescending(s => s.ModificationsSinceLastUpdate).FirstOrDefault();

    public static PageVerdict From(IReadOnlyList<StaleStatistic> stats)
    {
        var culture = CultureInfo.CurrentCulture;
        if (Worst(stats) is not { } worst)
            return new PageVerdict(VerdictLevel.Ok, "No statistic has changes pending",
                stats.Count == 0 ? "The connected database has no statistics with rows changed since their last update."
                    : string.Format(culture, "{0} listed, none with a row changed since its last update.", Sizes.Plural(stats.Count, "statistic", "statistics")));

        var stale = stats.Count(s => ChangedPct(s) >= StalePct);
        var name  = $"{worst.DatabaseName}.{worst.SchemaName}.{worst.TableName} ({worst.StatisticsName})";
        var changed = ChangedPct(worst) is { } pct
            ? string.Format(culture, "{0:N0} of its {1:N0} rows changed ({2:0} %)", worst.ModificationsSinceLastUpdate, worst.RowsInTable, pct)
            : string.Format(culture, "{0:N0} rows changed", worst.ModificationsSinceLastUpdate);
        var age = worst.LastUpdated is { } updated
            ? string.Format(culture, "last updated {0:yyyy-MM-dd}{1}", updated, worst.DaysOld is { } days ? $", {days:N0} days ago" : "")
            : "never updated";

        if (stale == 0)
            return new PageVerdict(VerdictLevel.Info,
                string.Format(culture, "No statistic has {0:0} % of its table changed", StalePct),
                string.Format(culture, "The most changed is {0}: {1}, {2}. SQL Server updates it on its own before long.", name, changed, age),
                [new(ShowWorst, "Show it")]);

        return new PageVerdict(VerdictLevel.Warning,
            string.Format(culture, "{0} {1:0} % or more of {2} table changed; the most, {3}",
                          Sizes.Plural(stale, "statistic has", "statistics have"), StalePct, stale == 1 ? "its" : "their", name),
            string.Format(culture, "{0}, {1}. The optimizer plans with row counts that are out of date, which can pick a scan or a small memory grant. " +
                          "Select rows and generate the UPDATE STATISTICS script, or let auto-update catch up.", changed, age),
            [new(ShowWorst, "Show it")]);
    }
}

/// <summary>Agent Jobs' verdict (#150): failed and long-running jobs, or Agent not running.</summary>
public static class AgentJobsVerdict
{
    /// <summary>Shows only the jobs that failed or run long.</summary>
    public const string NeedsAttention = "needs-attention";

    /// <summary>Selects the job the verdict names.</summary>
    public const string ShowJob = "show-job";

    /// <summary>The job the verdict names: the first failed, as the headline and its colour lead with them, else one running long.</summary>
    public static AgentJob? Subject(AgentJobList list) =>
        list.Jobs.FirstOrDefault(j => j.LastRunFailed) ?? list.Jobs.FirstOrDefault(j => j.IsRunningLong);

    public static PageVerdict From(AgentJobList list)
    {
        var culture = CultureInfo.CurrentCulture;
        if (!list.Available)
            return new PageVerdict(VerdictLevel.Unknown, "Couldn't read the SQL Agent jobs", list.Problem);

        var problem = string.IsNullOrWhiteSpace(list.Problem) ? "" : " " + list.Problem.Trim();
        if (list.Jobs.Count == 0)
            return new PageVerdict(VerdictLevel.Ok, "There are no SQL Agent jobs on this server", problem.Trim());

        var enabled = list.Jobs.Count(j => j.Enabled);
        if (list.Agent == AgentServiceState.Stopped && enabled > 0)
            return new PageVerdict(VerdictLevel.Critical, "SQL Server Agent is stopped: no job runs",
                string.Format(culture, "{0} enabled, none will start until Agent is started again; their next runs show as due.",
                              Sizes.Plural(enabled, "job is", "jobs are")) + problem);

        var failed = list.Jobs.Where(j => j.LastRunFailed).ToList();
        var longOnes = list.Jobs.Where(j => j.IsRunningLong).ToList();
        if (Subject(list) is not { } subject)
        {
            var next = list.Jobs.Where(j => j.Enabled && j.NextRun is not null).MinBy(j => j.NextRun);
            return new PageVerdict(VerdictLevel.Ok, "No job failed its last run, and none is running long",
                string.Format(culture, "{0}, {1} running now{2}.", Sizes.Plural(list.Jobs.Count, "job", "jobs"), list.RunningCount,
                              next is null ? "" : $"; next: {next.Name} at {next.NextRun:yyyy-MM-dd HH:mm} (server time)") + problem);
        }

        string Names(IReadOnlyList<AgentJob> jobs) =>
            string.Join(", ", jobs.Take(2).Select(j => j.Name)) + (jobs.Count > 2 ? string.Format(culture, " and {0:N0} more", jobs.Count - 2) : "");
        var parts = new List<string>();
        if (failed.Count > 0)
            parts.Add(string.Format(culture, "{0} {1} last run: {2}", Sizes.Plural(failed.Count, "job failed", "jobs failed"),
                                    failed.Count == 1 ? "its" : "their", Names(failed)));
        if (longOnes.Count > 0)
            parts.Add(string.Format(culture, "{0} running longer than usual: {1}",
                                    failed.Count > 0 ? Sizes.Plural(longOnes.Count, "is", "are") : Sizes.Plural(longOnes.Count, "job is", "jobs are"),
                                    Names(longOnes)));
        var headline = string.Join("; ", parts);

        var why = subject.IsRunningLong
            ? $"{subject.Name}: {subject.StatusDetail}"
            : $"{subject.Name}: {subject.StatusDetail}" + (string.IsNullOrWhiteSpace(subject.LastMessage) ? "" : " " + subject.LastMessage.Trim());
        return new PageVerdict(failed.Count > 0 ? VerdictLevel.Critical : VerdictLevel.Warning, headline, why + problem,
                               [new(ShowJob, "Show its runs"), new(NeedsAttention, "Only these")]);
    }
}
