using System.Globalization;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Engine.Verdicts;

/// <summary>Resource Queries' verdict (#149): the query that uses the most CPU, and the one that reads the most.</summary>
public static class ResourceQueriesVerdict
{
    /// <summary>Shows the top query by CPU and its details.</summary>
    public const string ShowCpu = "show-cpu";

    /// <summary>Shows the top query by logical reads and its details.</summary>
    public const string ShowReads = "show-reads";

    /// <summary>The top query's share of the top N's CPU above which it is said to dominate.</summary>
    public const double DominantShare = 0.5;

    public static PageVerdict From(IReadOnlyList<ResourceIntensiveQuery> byCpu, IReadOnlyList<ResourceIntensiveQuery> byReads)
    {
        var culture = CultureInfo.CurrentCulture;
        if (byCpu.Count == 0 && byReads.Count == 0)
            return new PageVerdict(VerdictLevel.Unknown, "The plan cache has no queries to rank",
                "It was cleared, the server has just started, or it keeps no statistics for this login.");

        List<VerdictAction> actions = [];
        string headline, why = "";
        var level = VerdictLevel.Info;
        if (byCpu.Count > 0)
        {
            var top   = byCpu.OrderByDescending(q => q.TotalCPUTime).First();
            var total = byCpu.Sum(q => (double)q.TotalCPUTime);
            var share = total > 0 ? top.TotalCPUTime / total : 0;
            headline = string.Format(culture, "{0} uses the most CPU: {1:0} % of the top {2:N0} queries'", Name(top, capital: true), share * 100, byCpu.Count);
            why = string.Format(culture, "{0:N0} runs, {1} of CPU each.", top.ExecutionCount, Ms(top.AvgCPUTime));
            if (share > DominantShare && byCpu.Count > 1)
                level = VerdictLevel.Warning;
            actions.Add(new(ShowCpu, "Show it"));
        }
        else
        {
            headline = "No query has used CPU worth ranking";
        }

        if (byReads.Count > 0)
        {
            var reader = byReads.OrderByDescending(q => q.TotalLogicalReads).First();
            var total  = byReads.Sum(q => (double)q.TotalLogicalReads);
            why += (why.Length > 0 ? " " : "") + string.Format(culture, "By reads, {0} reads the most: {1:N0} pages, {2:0} % of the top {3:N0}.",
                Name(reader, capital: false), reader.TotalLogicalReads, total > 0 ? reader.TotalLogicalReads / total * 100 : 0, byReads.Count);
            actions.Add(new(ShowReads, "Show the top reader"));
        }
        why += " Counted since each plan was cached.";
        return new PageVerdict(level, headline, why, actions);
    }

    // "dbo.usp_GetOrders", or "An ad hoc query (SELECT …)".
    private static string Name(ResourceIntensiveQuery q, bool capital)
    {
        if (!string.IsNullOrWhiteSpace(q.ObjectName))
            return q.ObjectName.Trim();
        var text = q.QueryText?.Trim() ?? "";
        var first = text.Length <= 40 ? text : text[..39] + "…";
        return (capital ? "An ad hoc query" : "an ad hoc query") + (first.Length > 0 ? $" ({first})" : "");
    }

    // Microseconds, as the DMV counts them, in words.
    internal static string Ms(long microseconds) => microseconds >= 1_000_000
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} s", microseconds / 1_000_000.0)
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} ms", microseconds / 1_000.0);
}

/// <summary>Query Regressions' verdict (#149): the queries that got slower in the recent period, the worst first.</summary>
public static class QueryRegressionsVerdict
{
    /// <summary>Opens Query Store on the worst query.</summary>
    public const string OpenQuery = "open-query";

    /// <summary>Selects the worst query and shows its plans in the drawer.</summary>
    public const string ShowPlans = "show-plans";

    /// <param name="rows">What the check found, the most extra time first.</param>
    /// <param name="compared">Both periods had queries to compare.</param>
    /// <param name="comparison">"Last 24 hours compared with the 7 days before".</param>
    /// <param name="notCompared">Why nothing could be compared, when it couldn't.</param>
    public static PageVerdict From(IReadOnlyList<QueryRegression> rows, bool compared, string comparison, string? notCompared,
                                   RegressionCriteria criteria)
    {
        var culture = CultureInfo.CurrentCulture;
        if (rows.Count == 0 && !compared)
            return new PageVerdict(VerdictLevel.Unknown, "Nothing to compare", notCompared ?? $"{comparison}: no query ran in both periods.");

        var rule = string.Format(culture, "none more than {0:0.##} % slower in duration or CPU, with at least {1:N0} executions in each period",
                                 criteria.ThresholdPct, criteria.MinExecutions);
        if (rows.Count == 0)
            return new PageVerdict(VerdictLevel.Ok, "No query got slower", $"{comparison}: {rule}.");

        var top  = rows.OrderByDescending(r => r.ExtraTimeMs).First();
        var plan = top.NewPlan ? " since it got a new plan" : top.PlanDiffers ? " with a different plan" : "";
        var headline = rows.Count == 1
            ? $"1 query got slower: query {top.QueryKey} {QueryStoreVerdict.Change(top)}{plan}"
            : string.Format(culture, "{0:N0} queries got slower; query {1} the most: {2}{3}", rows.Count, top.QueryKey, QueryStoreVerdict.Change(top), plan);
        var why = string.Format(culture, "{0}. Its slower runs cost {1} more in all.", comparison, Extra(top.ExtraTimeMs))
                + (top.NewPlan || top.PlanDiffers ? " Compare its plans now and before." : "");

        List<VerdictAction> actions = [new(ShowPlans, "Show its plans")];
        if (top.QueryStoreId is { } id)
            actions.Add(new(OpenQuery, $"Open query {id} in Query Store", id));
        return new PageVerdict(VerdictLevel.Warning, headline, why, actions);
    }

    private static string Extra(double ms) => ms switch
    {
        < 1_000     => string.Format(CultureInfo.CurrentCulture, "{0:N0} ms", ms),
        < 60_000    => string.Format(CultureInfo.CurrentCulture, "{0:0.#} s", ms / 1_000),
        < 3_600_000 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} min", ms / 60_000),
        _           => string.Format(CultureInfo.CurrentCulture, "{0:0.#} h", ms / 3_600_000),
    };
}

/// <summary>Plan Cache Health's verdict (#149): the worst of what it checks, by the page's own severity.</summary>
public static class PlanCacheHealthVerdict
{
    /// <summary>Shows a tab: <see cref="VerdictAction.Id"/> is its index.</summary>
    public const string ShowTab = "show-tab";

    /// <summary>Shows the Implicit Conversions tab with its most used plan selected (#178).</summary>
    public const string ShowWorstConversion = "show-worst-conversion";

    public const int MultiPlanTab           = 0;
    public const int KeyLookupTab           = 1;
    public const int PlanGuidesTab          = 4;
    public const int ImplicitConversionsTab = 5;

    /// <param name="conversions">The cached plans with CONVERT_IMPLICIT (#178); null when they weren't read.</param>
    public static PageVerdict From(PlanCacheHealthSummary summary, int keyLookups, IReadOnlyList<ImplicitConversion>? conversions = null)
    {
        var culture = CultureInfo.CurrentCulture;
        var level = summary.Severity switch
        {
            "CRITICAL" => VerdictLevel.Critical,
            "WARNING"  => VerdictLevel.Warning,
            _          => VerdictLevel.Ok,
        };
        var plans = string.Format(culture, "{0:N0} plans cached.", summary.TotalCachedPlans);

        if (summary.PlanCacheErasedRecently)
            return new PageVerdict(level == VerdictLevel.Ok ? VerdictLevel.Warning : level, "The plan cache was emptied in the last 24 hours",
                "DBCC FREEPROCCACHE or a restart: what this page counts covers only what ran since. Wait for plans to come back before drawing conclusions. " + plans);

        if (summary.MultiPlanQueryCount > 0)
            return new PageVerdict(level == VerdictLevel.Ok ? VerdictLevel.Warning : level,
                summary.MultiPlanQueryCount == 1
                    ? "1 query has more than 5 cached plans"
                    : string.Format(culture, "{0:N0} queries have more than 5 cached plans each", summary.MultiPlanQueryCount),
                "A query compiled again for each set of literal values isn't parameterised: it fills the cache and burns CPU on compiles. Parameterise it, or turn on Optimize for ad hoc workloads. " + plans,
                [new(ShowTab, "Multi-plan queries", MultiPlanTab)]);

        // A busy plan that converts implicitly scans on every run: before a plan guide that is only
        // a note, and before lookups, which cost less.
        var converting = conversions is { Count: > 0 } ? Conversions(conversions) : null;
        if (summary.PlanGuidesEnabled > 0)
        {
            var guides = new PageVerdict(level == VerdictLevel.Ok ? VerdictLevel.Info : level,
                summary.PlanGuidesEnabled == 1 ? "1 plan guide freezes a plan" : string.Format(culture, "{0:N0} plan guides freeze plans", summary.PlanGuidesEnabled),
                "A plan guide keeps a plan however the data or the indexes change: check that each still helps. " + plans,
                [new(ShowTab, "Plan guides", PlanGuidesTab)]);
            return converting is not null && converting.Level > guides.Level ? converting : guides;
        }

        if (converting is { Level: VerdictLevel.Warning })
            return converting;

        if (keyLookups > 0)
            return new PageVerdict(VerdictLevel.Info,
                "No parameterisation problems; " + (keyLookups == 1 ? "1 busy plan does key lookups" : string.Format(culture, "{0:N0} busy plans do key lookups", keyLookups)),
                "A key or RID lookup for every row found is often one INCLUDE column away from a covering index. " + plans,
                [new(ShowTab, "Key lookups", KeyLookupTab)]);

        if (converting is not null)
            return converting;

        return new PageVerdict(VerdictLevel.Ok, "The plan cache looks healthy",
            "No query with many plans, no plan guide, no busy plan doing lookups" + (conversions is null ? ". " : " and no plan converting implicitly. ") + plans);
    }

    // Implicit Conversions' verdict, with its action on this page's tab.
    private static PageVerdict Conversions(IReadOnlyList<ImplicitConversion> conversions)
    {
        var verdict = ImplicitConversionsVerdict.From(conversions);
        return new PageVerdict(verdict.Level, verdict.Headline, verdict.Why, [new(ShowWorstConversion, "Show it")]);
    }
}

/// <summary>Implicit Conversions' verdict (#149), a tab of Plan Cache Health since #178: how many cached plans convert implicitly, the most used first.</summary>
public static class ImplicitConversionsVerdict
{
    /// <summary>Selects the most used plan.</summary>
    public const string ShowWorst = "show-worst";

    /// <summary>Executions of a converting plan from which it is worth fixing first.</summary>
    public const int BusyExecutions = 1_000;

    /// <param name="rows">The cached plans with CONVERT_IMPLICIT.</param>
    /// <param name="cap">How many the page reads at most: when it reads that many, there may be more.</param>
    public static PageVerdict From(IReadOnlyList<ImplicitConversion> rows, int cap = 50)
    {
        var culture = CultureInfo.CurrentCulture;
        if (rows.Count == 0)
            return new PageVerdict(VerdictLevel.Ok, "No cached plan converts implicitly",
                "No plan in the cache has a CONVERT_IMPLICIT, so no seek is lost to a type mismatch.");

        var worst = rows.OrderByDescending(r => r.ExecutionCount).First();
        var count = rows.Count >= cap
            ? string.Format(culture, "At least {0:N0} cached plans convert implicitly", rows.Count)
            : rows.Count == 1 ? "1 cached plan converts implicitly" : string.Format(culture, "{0:N0} cached plans convert implicitly", rows.Count);
        var headline = string.Format(culture, "{0}; the most used ran {1:N0} times", count, worst.ExecutionCount);
        var where = string.IsNullOrWhiteSpace(worst.ObjectName)
            ? $"an ad hoc query{(string.IsNullOrWhiteSpace(worst.DatabaseName) ? "" : $" in {worst.DatabaseName}")}"
            : $"{(string.IsNullOrWhiteSpace(worst.DatabaseName) ? "" : worst.DatabaseName + ".")}{worst.ObjectName}";
        var why = $"The most used is {where}. A column converted to the parameter's type can't be sought in its index, so every run scans: send parameters of the column's type.";
        return new PageVerdict(worst.ExecutionCount >= BusyExecutions ? VerdictLevel.Warning : VerdictLevel.Info, headline, why,
                               [new(ShowWorst, "Show it")]);
    }
}

/// <summary>SP Trace's verdict (#149): what the capture is doing: running, stopped, or empty.</summary>
public static class SpTraceVerdict
{
    public const string Start = "start";
    public const string Stop  = "stop";

    /// <summary>Selects the slowest call captured.</summary>
    public const string ShowSlowest = "show-slowest";

    /// <param name="calls">The calls captured, unfiltered.</param>
    /// <param name="procedures">The procedures the By Procedure tab lists.</param>
    /// <param name="minDurationMs">The trace's minimum duration, for a running one with nothing yet.</param>
    public static PageVerdict From(TraceSessionStatus status, IReadOnlyList<SpTraceEvent> calls, int procedures, int minDurationMs = 0)
    {
        var culture = CultureInfo.CurrentCulture;
        var slowest = calls.Count == 0 ? null : calls.OrderByDescending(c => c.DurationMs).ThenByDescending(c => c.EventTime).First();
        var slowestText = slowest is null ? "" : string.Format(culture, "The slowest: {0}, {1:N0} ms (session {2}).",
            string.IsNullOrWhiteSpace(slowest.ObjectName) ? "a call without a procedure name" : slowest.ObjectName, slowest.DurationMs, slowest.SessionId);
        var byProcedure = procedures > 0 ? string.Format(culture, " By Procedure lists {0:N0} procedure{1} from the server's counters.", procedures, procedures == 1 ? "" : "s") : "";

        if (!status.IsRunning)
            return calls.Count == 0
                ? new PageVerdict(VerdictLevel.Info, "Not capturing",
                    "Start Trace captures stored-procedure calls as they complete; nothing runs on the server until you press it." + byProcedure,
                    [new(Start, "Start Trace")])
                : new PageVerdict(VerdictLevel.Info,
                    string.Format(culture, "Stopped, with {0:N0} call{1} captured", calls.Count, calls.Count == 1 ? "" : "s"),
                    slowestText + " Start Trace captures more; Clear empties the list.",
                    [new(Start, "Start Trace"), new(ShowSlowest, "Show the slowest")]);

        if (status.Mode == TraceMode.DmvFallback)
            return new PageVerdict(VerdictLevel.Info, "Capturing totals per procedure only",
                (status.Reason ?? "This login can't create an event session, so each call can't be captured.") + byProcedure,
                [new(Stop, "Stop Trace")]);

        List<VerdictAction> stop = [new(Stop, "Stop Trace")];
        if (status.EventsDropped > 0 || status.TargetTruncated)
            return new PageVerdict(VerdictLevel.Warning,
                status.EventsDropped > 0
                    ? string.Format(culture, "Capturing, but {0:N0} event{1} were dropped", status.EventsDropped, status.EventsDropped == 1 ? "" : "s")
                    : "Capturing, but some calls were not read",
                (status.EventsDropped > 0 ? "The ring buffer overflowed under the load. " : "The event target was truncated. ")
                + "Raise Min duration to capture fewer, slower calls. " + slowestText,
                slowest is null ? stop : [.. stop, new(ShowSlowest, "Show the slowest")]);

        if (calls.Count == 0)
            return new PageVerdict(VerdictLevel.Ok, "Capturing; no call yet",
                minDurationMs > 0
                    ? string.Format(culture, "Calls taking at least {0:N0} ms appear as they complete.", minDurationMs)
                    : "Calls appear as they complete.",
                stop);

        return new PageVerdict(VerdictLevel.Ok,
            string.Format(culture, "Capturing: {0:N0} call{1} so far", calls.Count, calls.Count == 1 ? "" : "s"),
            slowestText, [.. stop, new(ShowSlowest, "Show the slowest")]);
    }
}
