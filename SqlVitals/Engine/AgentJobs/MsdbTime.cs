using System.Globalization;

namespace SqlVitals.Engine.AgentJobs;

/// <summary>
/// msdb keeps job times as integers: <c>run_date</c> 20260925, <c>run_time</c> 143005 (14:30:05)
/// and <c>run_duration</c> 13005 (1 h 30 min 5 s). These read them; all are server local time.
/// </summary>
public static class MsdbTime
{
    /// <summary>A <c>run_date</c> and <c>run_time</c> pair; null for 0 (never) or a value that isn't a date.</summary>
    public static DateTime? ToDateTime(int runDate, int runTime)
    {
        if (runDate <= 0 || runTime < 0)
            return null;

        int year = runDate / 10000, month = runDate / 100 % 100, day = runDate % 100;
        int hour = runTime / 10000, minute = runTime / 100 % 100, second = runTime % 100;
        if (year is < 1753 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
            return null;

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// The <c>next_run_date * 1000000 + next_run_time</c> the jobs query builds, so the earliest
    /// schedule can be taken with one MIN on the server.
    /// </summary>
    public static DateTime? FromDateTimeNumber(long value) =>
        value <= 0 ? null : ToDateTime((int)(value / 1_000_000), (int)(value % 1_000_000));

    /// <summary>
    /// A <c>run_duration</c>: hhmmss, where the hours go past 99 for a run over four days.
    /// A negative value, which a clock change can leave, reads as zero.
    /// </summary>
    public static TimeSpan ToDuration(int runDuration) =>
        runDuration <= 0
            ? TimeSpan.Zero
            : new TimeSpan(runDuration / 10000, runDuration / 100 % 100, runDuration % 100);

    /// <summary>"0:00:45", "1:30:05", "27:03:10": hours, minutes and seconds, as SSMS shows job durations.</summary>
    public static string Format(TimeSpan? duration)
    {
        if (duration is not { } span)
            return string.Empty;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (long)span.TotalHours, span.Minutes, span.Seconds);
    }
}
