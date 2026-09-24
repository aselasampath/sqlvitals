using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Alerts;

public class AlertTextTests
{
    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(HealthIndicator.Cpu,                 93.4,  "93 %")]
    [InlineData(HealthIndicator.Blocking,            45,    "45 s")]
    [InlineData(HealthIndicator.Blocking,            212,   "3.5 min")]
    [InlineData(HealthIndicator.Blocking,            5_400, "1.5 h")]
    [InlineData(HealthIndicator.PageLifeExpectancy,  1250,  "1,250 s")]
    [InlineData(HealthIndicator.MemoryGrantsPending, 3,     "3")]
    [InlineData(HealthIndicator.LogSpace,            90,    "90 %")]
    [InlineData(HealthIndicator.TempDbSpace,         85.6,  "86 %")]
    public void Value_UsesTheIndicatorsUnit(HealthIndicator indicator, double value, string expected) =>
        Assert.Equal(expected, Invariant(() => AlertText.Value(indicator, value)));

    [Fact]
    public void Threshold_ReadsAsTheCheckDoes()
    {
        Assert.Equal("≥ 90 %", Invariant(() => AlertText.Threshold(HealthIndicator.Cpu, 90)));
        Assert.Equal("< 300 s", Invariant(() => AlertText.Threshold(HealthIndicator.PageLifeExpectancy, 300)));
        Assert.Equal("", AlertText.Threshold(HealthIndicator.Cpu, null));
    }

    [Theory]
    [InlineData(0,          "0 s")]
    [InlineData(59.9,       "59 s")]
    [InlineData(60,         "1 min")]
    [InlineData(3599,       "59 min")]
    [InlineData(3600 + 300, "1 h 05 min")]
    [InlineData(-5,         "0 s")]
    [InlineData(2 * 86_400 + 4 * 3600 + 59, "2 d 4 h")]
    public void Duration_ShowsTheTwoLargestUnits(double seconds, string expected) =>
        Assert.Equal(expected, Invariant(() => AlertText.Duration(TimeSpan.FromSeconds(seconds))));

    [Fact]
    public void Duration_OfAnActiveAlertRunsToNow()
    {
        var start = new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
        var alert = new Alert(Guid.NewGuid(), Guid.NewGuid(), HealthIndicator.Cpu, HealthLevel.Warning,
                              start, null, 80, 75, "CPU 80%");

        Assert.Equal(TimeSpan.FromMinutes(5), alert.Duration(start.AddMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(2), (alert with { EndedUtc = start.AddMinutes(2) }).Duration(start.AddMinutes(5)));
    }

    [Fact]
    public void Words_ForSeverityAndEndReason()
    {
        Assert.Equal("Critical", AlertText.Severity(HealthLevel.Critical));
        Assert.Equal("Warning", AlertText.Severity(HealthLevel.Warning));
        Assert.Equal("Back to normal", AlertText.EndReason(AlertEndReason.Recovered));
        Assert.Equal("Monitoring stopped", AlertText.EndReason(AlertEndReason.MonitoringStopped));
        Assert.Equal("", AlertText.EndReason(null));
    }

    [Fact]
    public void Settings_DefaultIsAmongTheChoices() =>
        Assert.Contains(AlertSettings.DefaultSamples, AlertSettings.SampleChoices);

    [Theory]
    [InlineData(0,   AlertSettings.DefaultSamples)]   // missing from an older settings file
    [InlineData(-4,  AlertSettings.DefaultSamples)]
    [InlineData(1,   1)]
    [InlineData(4,   3)]                              // a tie: the smaller choice
    [InlineData(7,   5)]
    [InlineData(500, 10)]
    public void Settings_NormalizePicksTheClosestChoice(int saved, int expected) =>
        Assert.Equal(expected, AlertSettings.NormalizeSamples(saved));
}
