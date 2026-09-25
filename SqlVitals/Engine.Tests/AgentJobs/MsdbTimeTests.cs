using SqlVitals.Engine.AgentJobs;

namespace SqlVitals.Engine.Tests.AgentJobs;

public class MsdbTimeTests
{
    [Fact]
    public void Reads_run_date_and_run_time()
    {
        Assert.Equal(new DateTime(2026, 9, 25, 14, 30, 5), MsdbTime.ToDateTime(20260925, 143005));
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 7), MsdbTime.ToDateTime(20260102, 7));
    }

    [Theory]
    [InlineData(0, 0)]          // never run
    [InlineData(20260231, 0)]   // 31 February
    [InlineData(20261301, 0)]   // month 13
    [InlineData(20260925, 246000)]
    [InlineData(20260925, -1)]
    public void Is_null_for_no_date_or_one_that_isnt_a_date(int runDate, int runTime) =>
        Assert.Null(MsdbTime.ToDateTime(runDate, runTime));

    [Fact]
    public void Reads_the_date_time_number_the_schedules_query_builds()
    {
        Assert.Equal(new DateTime(2026, 9, 26, 2, 0, 0), MsdbTime.FromDateTimeNumber(20260926_020000));
        Assert.Null(MsdbTime.FromDateTimeNumber(0));
    }

    [Theory]
    [InlineData(45, 0, 0, 45)]
    [InlineData(13005, 1, 30, 5)]
    [InlineData(1000000, 100, 0, 0)]  // 100 hours: hh goes past two digits
    [InlineData(0, 0, 0, 0)]
    [InlineData(-5, 0, 0, 0)]         // left by a clock change
    public void Reads_run_duration_as_hhmmss(int runDuration, int hours, int minutes, int seconds) =>
        Assert.Equal(new TimeSpan(hours, minutes, seconds), MsdbTime.ToDuration(runDuration));

    [Fact]
    public void Formats_durations_like_SSMS()
    {
        Assert.Equal("0:00:45", MsdbTime.Format(TimeSpan.FromSeconds(45)));
        Assert.Equal("1:30:05", MsdbTime.Format(new TimeSpan(1, 30, 5)));
        Assert.Equal("27:03:10", MsdbTime.Format(new TimeSpan(1, 3, 3, 10)));
        Assert.Equal("", MsdbTime.Format(null));
    }
}
