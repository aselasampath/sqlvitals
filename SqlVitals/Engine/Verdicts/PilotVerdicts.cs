using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Verdicts;

/// <summary>Processes' verdict (#146): the head blockers and how long they have blocked, or "No blocking".</summary>
public static class ProcessesVerdict
{
    /// <summary>Selects the head blocker of the chain waiting longest.</summary>
    public const string GoToHead = "go-to-head";

    /// <summary>Shows only the sessions in a chain.</summary>
    public const string ChainsOnly = "chains-only";

    /// <param name="nodes">The snapshot, as <see cref="BlockingChains.Classify"/> leaves it.</param>
    /// <param name="blocking">The connection's blocking threshold, which colours a chain; the default when null.</param>
    public static PageVerdict From(IReadOnlyList<ProcessNode> nodes, HealthThreshold? blocking = null)
    {
        var chains = BlockingChains.Find(nodes);
        if (chains.Count == 0)
            return new PageVerdict(VerdictLevel.Ok, "No blocking", nodes.Count switch
            {
                0     => "No user session is connected.",
                1     => "1 session, not waiting on another.",
                var n => string.Format(CultureInfo.CurrentCulture, "{0:N0} sessions, none waiting on another.", n),
            });

        var limits  = blocking ?? HealthThresholds.Info(HealthIndicator.Blocking).Default;
        var longest = chains.OrderByDescending(c => c.LongestWaitMs).ThenBy(c => c.HeadSessionId).First();
        var seconds = longest.LongestWaitMs / 1000.0;
        var level   = limits.Critical is { } critical && seconds >= critical ? VerdictLevel.Critical
                    : limits.Warning is { } warning && seconds >= warning   ? VerdictLevel.Warning
                    : VerdictLevel.Info;
        var blocked = chains.Sum(c => c.BlockedCount);

        string headline, why;
        if (chains.Count == 1)
        {
            headline = $"Session {longest.HeadSessionId} is blocking {Sessions(longest.BlockedCount)}, the longest for {Wait(longest.LongestWaitMs)}";
            why      = Head(nodes.First(n => n.SessionId == longest.HeadSessionId));
        }
        else
        {
            headline = $"{chains.Count} head blockers are blocking {Sessions(blocked)}; session {longest.HeadSessionId} the longest, for {Wait(longest.LongestWaitMs)}";
            var listed = chains.Take(3).Select(c => $"{c.HeadSessionId}: {c.BlockedCount} blocked, {Wait(c.LongestWaitMs)}");
            why = "Head blockers " + string.Join(" · ", listed) + (chains.Count > 3 ? $" · and {chains.Count - 3} more." : ".")
                + " " + Head(nodes.First(n => n.SessionId == longest.HeadSessionId));
        }

        return new PageVerdict(level, headline, why,
        [
            new(GoToHead, $"Go to session {longest.HeadSessionId}", longest.HeadSessionId),
            new(ChainsOnly, "Blocking chains only"),
        ]);
    }

    // What the head is doing: a sleeping session holding a transaction is the classic forgotten commit.
    private static string Head(ProcessNode head)
    {
        var who = string.Join(", ", new[] { head.ProgramName, head.UserName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var from = who.Length == 0 ? "" : $" ({who})";
        if (string.Equals(head.Status, "sleeping", StringComparison.OrdinalIgnoreCase) && head.OpenTran > 0)
            return $"Session {head.SessionId}{from} is sleeping with an open transaction: the application most likely didn't commit.";
        return string.IsNullOrWhiteSpace(head.Status)
            ? $"Session {head.SessionId}{from} heads it."
            : $"Session {head.SessionId}{from} is {head.Status.Trim().ToLowerInvariant()}.";
    }

    private static string Sessions(int count) => count == 1 ? "1 session" : $"{count:N0} sessions";

    // "45 s", "4.1 min": as the health dot and the alerts word blocking.
    private static string Wait(long ms) => AlertText.Value(HealthIndicator.Blocking, ms / 1000.0);
}

/// <summary>Query Store's verdict (#146): the queries that got slower in the window against the baseline.</summary>
public static class QueryStoreVerdict
{
    /// <summary>Opens the slowed query's details.</summary>
    public const string OpenQuery = "open-query";

    /// <summary>Opens Query Regressions.</summary>
    public const string Regressions = "regressions";

    /// <param name="regressions">What the check found, the most extra time first; null when the period
    /// can't be compared (a custom one).</param>
    /// <param name="comparison">"Last 24 hours compared with the 7 days before".</param>
    /// <param name="notComparedWhy">Why there are no <paramref name="regressions"/> when it isn't the period:
    /// the regression check's statistics couldn't be read.</param>
    public static PageVerdict From(QueryStoreStatus status, IReadOnlyList<QueryRegression>? regressions, string comparison,
                                   Regressions.RegressionCriteria criteria, string? notComparedWhy = null)
    {
        if (!status.IsAvailable)
            return new PageVerdict(VerdictLevel.Unknown, $"Query Store isn't recording for {status.DatabaseName}",
                                   status.UnavailableReason);

        if (regressions is null)
            return notComparedWhy is not null
                ? new PageVerdict(VerdictLevel.Unknown, "Couldn't compare with the 7 days before", notComparedWhy)
                : new PageVerdict(VerdictLevel.Info, "Not compared with a baseline",
                    "Pick Last hour, Last 24 hours, Last 7 days or Last 30 days to compare the period with the 7 days before it.",
                    [new(Regressions, "Query Regressions")]);

        var culture = CultureInfo.CurrentCulture;
        var rule    = string.Format(culture, "none more than {0:0.##} % slower, with at least {1:N0} executions in each period",
                                    criteria.ThresholdPct, criteria.MinExecutions);
        if (regressions.Count == 0)
            return new PageVerdict(VerdictLevel.Ok, "No query got slower", $"{comparison}: {rule}.");

        var top    = regressions[0];
        var change = Change(top);
        var plan   = top.NewPlan ? " since it got a new plan" : top.PlanDiffers ? " with a different plan" : "";
        var headline = regressions.Count == 1
            ? $"1 query got slower: query {top.QueryKey} {change}{plan}"
            : $"{regressions.Count:N0} queries got slower; query {top.QueryKey} the most: {change}{plan}";

        return new PageVerdict(VerdictLevel.Warning, headline,
            $"{comparison}. Query Regressions lists each with its plans.",
            top.QueryStoreId is { } id
                ? [new(OpenQuery, $"Open query {id}", id), new(Regressions, "Query Regressions")]
                : [new(Regressions, "Query Regressions")]);
    }

    // "runs 4.1× slower" on duration, else on CPU; without a factor when the baseline was zero.
    internal static string Change(QueryRegression r)
    {
        var (pct, what) = r.DurationRegressed ? (r.DurationChangePct, "runs") : (r.CpuChangePct, "uses CPU");
        var factor = pct is { } p ? 1 + p / 100 : (double?)null;
        return factor is { } f
            ? string.Format(CultureInfo.CurrentCulture, r.DurationRegressed ? "runs {0:0.#}× slower" : "uses {0:0.#}× the CPU", f)
            : r.DurationRegressed ? "runs slower" : $"{what} more";
    }
}

/// <summary>Backups' verdict (#146): the databases past the RPO, or "Every database is within your RPO".</summary>
public static class BackupsVerdict
{
    /// <summary>Shows only the databases that need attention.</summary>
    public const string ShowThem = "show-them";

    public static PageVerdict From(BackupList list)
    {
        if (!list.Available)
            return new PageVerdict(VerdictLevel.Unknown, "Backups can't be checked here", list.Problem);

        var checkedCount = list.Databases.Count(d => d.IsOnline);
        var notOnline    = list.NotCheckedCount > 0 ? $" {list.NotCheckedCount:N0} not online, so not checked." : "";
        if (checkedCount == 0)
            return new PageVerdict(VerdictLevel.Unknown, "No online database to check", notOnline.Trim());

        var late = BackupList.Sort(list.Databases.Where(d => d.NeedsAttention)).ToList();
        if (late.Count == 0)
            return new PageVerdict(VerdictLevel.Ok, "Every database is within your RPO",
                $"{(checkedCount == 1 ? "1 database" : $"{checkedCount:N0} databases")} checked for {list.Rpo.Describe()}.{notOnline}");

        var level    = late.Any(d => d.IsCritical) ? VerdictLevel.Critical : VerdictLevel.Warning;
        var headline = late.Count == 1
            ? $"{late[0].Name} is past your RPO: {late[0].StatusText.ToLowerInvariant()}"
            : $"{late.Count:N0} databases are past your RPO";
        var listed = late.Take(3).Select(d => $"{d.Name}: {d.StatusText.ToLowerInvariant()}");
        var why = (late.Count == 1 ? late[0].WarningsText : string.Join(" · ", listed) + (late.Count > 3 ? $" · and {late.Count - 3:N0} more" : "") + ".")
                + $" Checked for {list.Rpo.Describe()}.";

        return new PageVerdict(level, headline, why, [new(ShowThem, late.Count == 1 ? "Show only it" : "Show only these")]);
    }
}
