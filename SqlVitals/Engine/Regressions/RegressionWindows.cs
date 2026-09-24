namespace SqlVitals.Engine.Regressions;

/// <summary>What the recent period of a regression check is compared with.</summary>
public enum RegressionBaselineKind
{
    /// <summary>The 7 days just before the recent period: many executions, so a steady average.</summary>
    PreviousWeek,

    /// <summary>The same period a week earlier, as the trend pages' baseline (#31) compares.</summary>
    SameTimeLastWeek,
}

/// <summary>
/// The two periods a regression check compares, both ending relative to "now": the recent one,
/// [now - <see cref="Recent"/>, now), and the baseline before it. Kept as offsets so Query Store
/// can place them on the server's clock and the monitoring history on this PC's.
/// </summary>
public sealed record RegressionWindows(TimeSpan Recent, RegressionBaselineKind Baseline)
{
    /// <summary>How far back the baseline reaches past the recent period.</summary>
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public static IReadOnlyList<TimeSpan> RecentChoices { get; } =
        [TimeSpan.FromHours(1), TimeSpan.FromHours(4), TimeSpan.FromHours(24)];

    public static RegressionWindows Default { get; } = new(TimeSpan.FromHours(24), RegressionBaselineKind.PreviousWeek);

    /// <summary>How long before now the baseline starts; the same for both kinds.</summary>
    public TimeSpan BaselineStartAgo => Recent + Week;

    /// <summary>How long before now the baseline ends: where the recent period starts, or a week back.</summary>
    public TimeSpan BaselineEndAgo => Baseline == RegressionBaselineKind.PreviousWeek ? Recent : Week;

    /// <summary>The UTC times of both periods, each end excluded.</summary>
    public (DateTime RecentFrom, DateTime RecentTo, DateTime BaselineFrom, DateTime BaselineTo) Resolve(DateTime nowUtc)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return (nowUtc - Recent, nowUtc, nowUtc - BaselineStartAgo, nowUtc - BaselineEndAgo);
    }

    /// <summary>"Last hour", "Last 4 hours", "Last 24 hours".</summary>
    public string RecentLabel => Recent == TimeSpan.FromHours(1) ? "Last hour" : $"Last {Recent.TotalHours:0} hours";

    /// <summary>"the 7 days before" or "the same time last week".</summary>
    public string BaselineLabel => Baseline == RegressionBaselineKind.PreviousWeek
        ? "the 7 days before"
        : "the same time last week";

    /// <summary>"Last 24 hours compared with the 7 days before".</summary>
    public string Describe() => $"{RecentLabel} compared with {BaselineLabel}";
}
