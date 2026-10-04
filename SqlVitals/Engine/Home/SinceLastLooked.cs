using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Engine.Home;

/// <summary>
/// The period "Since you last looked" covers (#143): from when this server's Overview was last
/// opened to now, UTC on this PC's clock.
/// </summary>
/// <param name="LastLookedUtc">When the Overview was last opened; null the first time.</param>
/// <param name="Capped">The last look was longer ago than <see cref="SinceLastLooked.Longest"/>, so the window starts there.</param>
public sealed record LookWindow(DateTime FromUtc, DateTime ToUtc, DateTime? LastLookedUtc, bool Capped)
{
    public bool IsFirstLook => LastLookedUtc is null;

    public bool Contains(DateTime utc) => utc >= FromUtc && utc <= ToUtc;
}

/// <summary>
/// What happened on a server since its Overview was last opened (#143): deadlocks, Agent jobs
/// that failed, and queries that got slower. Pure: the Overview reads the server and the history.
/// </summary>
public static class SinceLastLooked
{
    /// <summary>The window the first time a server's Overview is opened.</summary>
    public static readonly TimeSpan FirstLook = TimeSpan.FromHours(24);

    /// <summary>
    /// The longest window: system_health keeps a few days of deadlocks, and a regression check
    /// compares with the week before the window.
    /// </summary>
    public static readonly TimeSpan Longest = TimeSpan.FromDays(7);

    public static LookWindow Window(DateTime? lastLookedUtc, DateTime nowUtc)
    {
        if (lastLookedUtc is not { } last)
            return new LookWindow(nowUtc - FirstLook, nowUtc, null, Capped: false);
        if (last > nowUtc)
            // A clock put back: nothing can have happened after now.
            return new LookWindow(nowUtc, nowUtc, last, Capped: false);
        return nowUtc - last > Longest
            ? new LookWindow(nowUtc - Longest, nowUtc, last, Capped: true)
            : new LookWindow(last, nowUtc, last, Capped: false);
    }

    /// <summary>The deadlocks in the window, newest first.</summary>
    public static IReadOnlyList<DeadlockReport> Deadlocks(IEnumerable<DeadlockReport> deadlocks, LookWindow window) =>
        deadlocks.Where(d => window.Contains(d.TimestampUtc))
                 .OrderByDescending(d => d.TimestampUtc)
                 .ToList();

    /// <summary>
    /// The jobs whose last run failed and ended in the window, the latest first. msdb keeps run
    /// times on the server's clock; <paramref name="serverOffset"/> (see <see cref="ServerClock"/>)
    /// puts them on UTC. Only each job's last run is known here, so a job that failed and then
    /// succeeded since isn't counted.
    /// </summary>
    public static IReadOnlyList<AgentJob> FailedJobs(IEnumerable<AgentJob> jobs, LookWindow window, TimeSpan serverOffset) =>
        jobs.Where(j => j.LastRunFailed && EndedUtc(j, serverOffset) is { } ended && window.Contains(ended))
            .OrderByDescending(j => EndedUtc(j, serverOffset))
            .ToList();

    /// <summary>When a job's last run ended, UTC; null when it hasn't run.</summary>
    public static DateTime? EndedUtc(AgentJob job, TimeSpan serverOffset) =>
        job.LastRunStart is { } start
            ? DateTime.SpecifyKind(start + (job.LastDuration ?? TimeSpan.Zero) - serverOffset, DateTimeKind.Utc)
            : null;

    /// <summary>
    /// The period a regression check compares the window with: the week before it, as the Query
    /// Regressions page's default does (see <see cref="RegressionWindows"/>).
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc) Baseline(LookWindow window) =>
        (window.FromUtc - RegressionWindows.Week, window.FromUtc);
}

/// <summary>Moving between this PC's clock (UTC) and a server's, which may be in another time zone.</summary>
public static class ServerClock
{
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The server's offset from UTC, from a time read on the server and this PC's UTC time at about
    /// the same moment. Rounded to the quarter hour every time zone is a multiple of, so the time
    /// the query took doesn't show.
    /// </summary>
    public static TimeSpan Offset(DateTime serverTime, DateTime utcNow) =>
        TimeSpan.FromTicks((long)Math.Round((serverTime - utcNow).Ticks / (double)Quarter.Ticks) * Quarter.Ticks);
}
