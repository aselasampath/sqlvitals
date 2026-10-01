using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Home;

/// <summary>"What happened" for a server that can't be reached (#130).</summary>
public sealed class OutageTimelineTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 21, 40, 58, DateTimeKind.Utc);
    private static readonly Guid Id = Guid.NewGuid();

    private static ActivityEntry Row(DateTime time, string evt, string? detail = null) =>
        new(time, evt, Id, "DR replica", ActivitySources.AutomaticAtStartup, Detail: detail);

    [Fact]
    public void TellsTheStory_InOrder_EndingWithStillFailing()
    {
        var alert = new Alert(Guid.NewGuid(), Id, HealthIndicator.Cpu, HealthLevel.Warning, T0.AddMinutes(-20),
                              T0.AddMinutes(1), 80, 75, "CPU 80%", AlertEndReason.MonitoringStopped);

        var events = OutageTimeline.Build(
            (T0, 100), T0.AddSeconds(7), failures: 28, nextTryUtc: T0.AddMinutes(15),
            [Row(T0.AddHours(-1), ActivityEvents.MonitoringStarted, "Monitor in background is on."),
             Row(T0.AddMinutes(-5), ActivityEvents.SettingChanged)],                    // not part of the story
            [alert],
            nowUtc: T0.AddMinutes(14));

        Assert.Equal(
        [
            "Monitoring started: Monitor in background is on.",
            "Warning alert started: CPU",
            "Last good sample, score 100",
            "Collections started failing: the server couldn't be reached or read",
            "Alert ended: CPU, monitoring stopped",
            "Still failing after 28 failed attempts",
        ], events.Select(e => e.Text));
    }

    [Fact]
    public void WithNoGoodSample_AndMonitoringStopped_SaysSo()
    {
        var events = OutageTimeline.Build(null, T0, failures: 1, nextTryUtc: null, [], [], T0.AddSeconds(30));

        Assert.Equal(["Collections started failing: the server couldn't be reached or read",
                      "Still failing after 1 failed attempt; monitoring is stopped"], events.Select(e => e.Text));
    }

    [Fact]
    public void KeepsOnlyTheMostRecentEvents()
    {
        var rows = Enumerable.Range(1, 20).Select(i => Row(T0.AddMinutes(-i), ActivityEvents.MonitoringStarted));

        var events = OutageTimeline.Build((T0, 90), T0.AddSeconds(5), 3, T0.AddMinutes(1), rows, [], T0.AddMinutes(1));

        Assert.Equal(OutageTimeline.MaxEvents, events.Count);
        Assert.Equal("Still failing after 3 failed attempts", events[^1].Text);
    }
}
