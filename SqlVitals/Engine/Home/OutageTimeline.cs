using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Home;

/// <summary>One line of <see cref="OutageTimeline"/>: when, on this PC's UTC clock, and what.</summary>
public sealed record TimelineEvent(DateTime TimeUtc, string Text);

/// <summary>
/// "What happened" on the detail page of a server that can't be reached (#130): the last good
/// sample, when collections started failing, what monitoring and the alerts did meanwhile, and
/// that it's still failing. Newest last; the most recent <see cref="MaxEvents"/>.
/// </summary>
public static class OutageTimeline
{
    public const int MaxEvents = 8;

    /// <param name="lastGood">The last collection that worked, and its score; null when none has.</param>
    /// <param name="failingSinceUtc">When collections started failing.</param>
    /// <param name="failures">Collections in a row that have failed.</param>
    /// <param name="nextTryUtc">When the next collection is due; null when monitoring is stopped.</param>
    /// <param name="activity">This connection's activity log rows around the outage.</param>
    /// <param name="alerts">This connection's alerts around the outage.</param>
    public static IReadOnlyList<TimelineEvent> Build(
        (DateTime TimeUtc, int Score)? lastGood,
        DateTime                       failingSinceUtc,
        int                            failures,
        DateTime?                      nextTryUtc,
        IEnumerable<ActivityEntry>     activity,
        IEnumerable<Alert>             alerts,
        DateTime                       nowUtc)
    {
        var events = new List<TimelineEvent>();

        if (lastGood is { } good)
            events.Add(new(good.TimeUtc, $"Last good sample, score {good.Score}"));

        events.Add(new(failingSinceUtc, "Collections started failing: the server couldn't be reached or read"));

        foreach (var row in activity)
            if (row.Event is ActivityEvents.MonitoringStarted or ActivityEvents.MonitoringStopped or ActivityEvents.ConnectionEdited)
                events.Add(new(row.TimeUtc, row.Detail is { Length: > 0 } detail ? $"{row.Event}: {detail}" : row.Event));

        foreach (var alert in alerts)
        {
            var name = HealthThresholds.Info(alert.Indicator).Name;
            events.Add(new(alert.StartedUtc, $"{AlertText.Severity(alert.Severity)} alert started: {name}"));
            if (alert.EndedUtc is { } ended)
                events.Add(new(ended, AlertText.EndReason(alert.EndReason) is { Length: > 0 } why
                    ? $"Alert ended: {name}, {why.ToLowerInvariant()}"
                    : $"Alert ended: {name}"));
        }

        var attempts = failures == 1 ? "1 failed attempt" : $"{failures} failed attempts";
        events.Add(new(nowUtc, nextTryUtc is null
            ? $"Still failing after {attempts}; monitoring is stopped"
            : $"Still failing after {attempts}"));

        return events
            .OrderBy(e => e.TimeUtc)
            .TakeLast(MaxEvents)
            .ToList();
    }
}
