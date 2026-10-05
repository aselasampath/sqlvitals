using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Verdicts;

/// <summary>
/// Active Waits' verdict (#149): the requests blocked and the longest wait, or what most requests
/// wait on, or that nothing waits.
/// </summary>
public static class ActiveWaitsVerdict
{
    /// <summary>Opens Processes on the blocking chain.</summary>
    public const string Processes = "processes";

    /// <param name="waits">Every user request now, waiting or not.</param>
    /// <param name="blocking">The connection's blocking threshold, which colours a block; the default when null.</param>
    public static PageVerdict From(IReadOnlyList<ActiveWait> waits, HealthThreshold? blocking = null)
    {
        var culture = CultureInfo.CurrentCulture;
        var waiting = waits.Where(w => !string.IsNullOrWhiteSpace(w.WaitType)).ToList();
        if (waiting.Count == 0)
            return new PageVerdict(VerdictLevel.Ok, "No request is waiting", waits.Count switch
            {
                0     => "No user request is running now.",
                1     => "1 request is running, not waiting on anything.",
                var n => string.Format(culture, "{0:N0} requests are running, none waiting on anything.", n),
            });

        var blocked = waiting.Where(w => w.BlockingSessionId > 0).ToList();
        if (blocked.Count > 0)
        {
            var limits  = blocking ?? HealthThresholds.Info(HealthIndicator.Blocking).Default;
            var longest = blocked.OrderByDescending(w => w.WaitTimeMs).ThenBy(w => w.SessionId).First();
            var seconds = longest.WaitTimeMs / 1000.0;
            var level   = limits.Critical is { } critical && seconds >= critical ? VerdictLevel.Critical
                        : limits.Warning is { } warning && seconds >= warning   ? VerdictLevel.Warning
                        : VerdictLevel.Info;
            var heads   = blocked.Select(w => w.BlockingSessionId).Distinct().Count();
            var headline = blocked.Count == 1
                ? $"Session {longest.SessionId} is blocked by session {longest.BlockingSessionId}, for {Wait(longest.WaitTimeMs)}"
                : string.Format(culture, "{0:N0} requests are blocked; session {1} the longest, for {2}",
                                blocked.Count, longest.SessionId, Wait(longest.WaitTimeMs));
            var why = $"It waits on {longest.WaitType}" + (string.IsNullOrWhiteSpace(longest.WaitResource) ? "" : $" ({longest.WaitResource.Trim()})")
                    + (heads == 1 ? $", held by session {longest.BlockingSessionId}." : string.Format(culture, "; {0:N0} sessions hold the locks.", heads))
                    + " Processes shows the whole chain.";
            return new PageVerdict(level, headline, why, [new(Processes, "Blocking in Processes", longest.BlockingSessionId)]);
        }

        // Nothing blocked: what most of them wait on, and the longest wait.
        var top = waiting.GroupBy(w => w.WaitCategory)
                         .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Sum(w => w.WaitTimeMs)).First();
        var slowest = waiting.OrderByDescending(w => w.WaitTimeMs).First();
        var headlineOf = waiting.Count == 1
            ? $"1 request is waiting, on {slowest.WaitType}"
            : string.Format(culture, "{0:N0} requests are waiting, {1} on {2}", waiting.Count, top.Count() == waiting.Count ? "all" : $"{top.Count():N0}", Category(top.Key));
        var whyOf = $"None is blocked. The longest wait: session {slowest.SessionId} on {slowest.WaitType}, {Wait(slowest.WaitTimeMs)}.";
        // A wait of over a minute on anything is worth a look; shorter ones come and go.
        return new PageVerdict(slowest.WaitTimeMs >= 60_000 ? VerdictLevel.Warning : VerdictLevel.Info, headlineOf, whyOf);
    }

    private static string Category(string category) => category switch
    {
        "Other" => "other waits",
        var c   => $"{c} waits",
    };

    // "45 s", "4.1 min": as the health dot and the alerts word blocking.
    internal static string Wait(long ms) => ms < 1_000
        ? string.Format(CultureInfo.CurrentCulture, "{0:N0} ms", ms)
        : AlertText.Value(HealthIndicator.Blocking, ms / 1000.0);
}

/// <summary>Deadlocks' verdict (#149): how many in the range and the object they fight over most.</summary>
public static class DeadlocksVerdict
{
    /// <summary>Selects the newest deadlock of the most frequent pattern.</summary>
    public const string OpenNewest = "open-newest";

    /// <summary>Shows the Recurring tab.</summary>
    public const string Recurring = "recurring";

    /// <summary>Widens the range to everything system_health holds.</summary>
    public const string ShowAll = "show-all";

    /// <param name="history">What system_health held.</param>
    /// <param name="inRange">The deadlocks in the range shown, newest first.</param>
    /// <param name="patterns">Those deadlocks by pattern, the most frequent first.</param>
    /// <param name="during">"in the last 24 hours", "in what system_health holds".</param>
    public static PageVerdict From(DeadlockHistory history, IReadOnlyList<DeadlockReport> inRange,
                                   IReadOnlyList<DeadlockPattern> patterns, string during)
    {
        var culture = CultureInfo.CurrentCulture;
        if (history.Source == DeadlockSource.None)
            return new PageVerdict(VerdictLevel.Unknown, "The deadlock history can't be read",
                                   history.Problem ?? "system_health couldn't be read.");

        var partly = history.Problem is null ? "" : " " + history.Problem.Trim();
        if (inRange.Count == 0)
        {
            if (history.Deadlocks.Count == 0)
                return new PageVerdict(VerdictLevel.Ok, "No deadlocks",
                    "system_health holds no deadlock report: none happened in the time it covers." + partly);
            return new PageVerdict(VerdictLevel.Ok, $"No deadlocks {during}",
                string.Format(culture, "system_health holds {0:N0} older.", history.Deadlocks.Count) + partly,
                [new(ShowAll, "Show the older ones")]);
        }

        // The object fought over in most deadlocks: each deadlock counts it once, without the index.
        var (top, count) = inRange
            .SelectMany(r => r.Resources.Select(res => res.ObjectName ?? res.Kind).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(o => o, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Object: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Object, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var on = top is null ? "" : inRange.Count == 1 ? $", on {top}" : count == inRange.Count ? $", all on {top}" : string.Format(culture, "; {0} in {1:N0} of them", top, count);
        var headline = inRange.Count == 1
            ? $"1 deadlock {during}{on}"
            : string.Format(culture, "{0:N0} deadlocks {1}{2}", inRange.Count, during, on);

        var most = patterns.Count > 0 ? patterns[0] : null;
        var why = most is { Count: > 1 }
            ? string.Format(culture, "The same deadlock came back {0:N0} times, last at {1:HH:mm}: {2}. Fix it first.", most.Count, most.LastSeen, Code(most))
            : inRange.Count == 1 ? "The victim's statement was rolled back with error 1205." : "No two look alike: each is a different pattern.";

        List<VerdictAction> actions = [new(OpenNewest, most is { Count: > 1 } ? "Open the newest of them" : "Open the newest")];
        if (patterns.Count > 1 || most is { Count: > 1 })
            actions.Add(new(Recurring, "Recurring"));
        return new PageVerdict(VerdictLevel.Warning, headline, why + partly, actions);
    }

    private static string Code(DeadlockPattern pattern) =>
        pattern.Code.Length == 0 ? pattern.Objects : pattern.Code.Length <= 120 ? pattern.Code : pattern.Code[..119] + "…";
}

/// <summary>App Connections' verdict (#149): who holds the connections, and a pool that looks leaked.</summary>
public static class AppConnectionsVerdict
{
    /// <summary>Connections one application holds before an idle pool counts as a possible leak.</summary>
    public const int LeakConnections = 200;

    /// <summary>The share of those connections running a request below which the pool looks idle.</summary>
    public const double LeakActiveShare = 0.05;

    public static PageVerdict From(IReadOnlyList<ApplicationConnection> apps)
    {
        var culture = CultureInfo.CurrentCulture;
        if (apps.Count == 0)
            return new PageVerdict(VerdictLevel.Info, "No user connections", "No application is connected now.");

        var total  = apps.Sum(a => a.CurrentConnections);
        var active = apps.Sum(a => a.ActiveRequests);
        var top    = apps.OrderByDescending(a => a.CurrentConnections).ThenBy(a => a.ApplicationName, StringComparer.OrdinalIgnoreCase).First();

        var leaked = apps.Where(a => a.CurrentConnections >= LeakConnections && a.ActiveRequests < a.CurrentConnections * LeakActiveShare)
                         .OrderByDescending(a => a.CurrentConnections).FirstOrDefault();
        if (leaked is not null)
            return new PageVerdict(VerdictLevel.Warning,
                string.Format(culture, "{0} holds {1:N0} connections, {2:N0} of them running a request", Name(leaked), leaked.CurrentConnections, leaked.ActiveRequests),
                "So many idle connections from one application usually mean a pool that isn't returned to (a connection leak) or a pool much larger than it needs. Each holds memory on the server.");

        var apps1 = apps.Count == 1 ? "1 application holds" : string.Format(culture, "{0:N0} applications hold", apps.Count);
        var headline = string.Format(culture, "{0} {1:N0} connection{2}", apps1, total, total == 1 ? "" : "s")
                     + (apps.Count == 1 ? "" : string.Format(culture, "; {0} the most, {1:N0}", Name(top), top.CurrentConnections));
        var why = active switch
        {
            0     => "None is running a request now.",
            1     => "1 is running a request now.",
            var n => string.Format(culture, "{0:N0} are running a request now.", n),
        };
        return new PageVerdict(VerdictLevel.Ok, headline, why);
    }

    /// <summary>The application's name, or what to call one without.</summary>
    public static string Name(ApplicationConnection app) =>
        string.IsNullOrWhiteSpace(app.ApplicationName) ? "An application without a name" : app.ApplicationName.Trim();
}
