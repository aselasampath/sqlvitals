using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Alerts;

public class AlertTrackerTests
{
    private static readonly Guid     ConnectionId = Guid.Parse("6f1d2a8e-0000-4000-8000-000000000001");
    private static readonly DateTime T0           = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    private readonly AlertTracker _tracker = new(ConnectionId, samples: 3);
    private int _sample;

    // A quiet server: nothing near any default threshold.
    private static LiveMetricSample Quiet(double cpuPct = 10, double ple = 5000, double blocked = 0, double blockSec = 0) =>
        new(T0, 0, 0, 0, 0, 0, 0, cpuPct, 0, 0, 0, 0, 0, 0, ple, 0, 99.9,
            blocked, blockSec, LogUsedPct: 5, LogUsedDatabase: "Sales", TempDbUsedPct: 10);

    private static DateTime At(int sample) => T0.AddSeconds(10 * sample);

    // Takes the next sample (10 s after the previous one) on the default thresholds.
    private IReadOnlyList<AlertChange> Next(LiveMetricSample sample) =>
        _tracker.Evaluate(HealthRules.Grade(sample, HealthThresholds.Default), At(_sample++));

    private IReadOnlyList<AlertChange> Cpu(double pct) => Next(Quiet(cpuPct: pct));

    private List<AlertChange> CpuRun(params double[] pcts) => pcts.SelectMany(Cpu).ToList();

    // ── Starting ──────────────────────────────────────────────────────────────

    [Fact]
    public void Starts_OnlyAfterTheSetNumberOfSamplesInARow()
    {
        Assert.Empty(Cpu(80));
        Assert.Empty(Cpu(80));
        var change = Assert.Single(Cpu(80));

        Assert.Equal(AlertChangeKind.Started, change.Kind);
        var alert = change.Alert;
        Assert.Equal(ConnectionId, alert.ConnectionId);
        Assert.Equal(HealthIndicator.Cpu, alert.Indicator);
        Assert.Equal(HealthLevel.Warning, alert.Severity);
        Assert.True(alert.IsActive);
        Assert.Null(alert.EndReason);
        Assert.Equal(75, alert.Threshold);
        Assert.Single(_tracker.Active);
    }

    [Fact]
    public void Starts_AtTheFirstSampleOfTheBreachNotWhenItFires()
    {
        CpuRun(10, 80, 80);
        var started = Assert.Single(Cpu(80)).Alert;

        Assert.Equal(At(1), started.StartedUtc);
    }

    [Fact]
    public void HoveringAroundAThreshold_NeverStartsAnAlert()
    {
        // Past the threshold every other sample, or twice then once not: never three in a row.
        var changes = CpuRun(80, 70, 80, 70, 80, 80, 70, 80, 80, 74, 76, 76, 74.9);

        Assert.Empty(changes);
        Assert.Empty(_tracker.Active);
    }

    [Fact]
    public void HoveringAfterItStarted_KeepsOneAlertInsteadOfStartingNewOnes()
    {
        var first = CpuRun(80, 80, 80).Single().Alert;

        // Dips below the threshold, never for three samples in a row.
        var changes = CpuRun(70, 80, 70, 70, 80, 70, 80, 80, 80, 70, 70, 76);

        Assert.DoesNotContain(changes, c => c.Kind is AlertChangeKind.Started or AlertChangeKind.Ended);
        Assert.Equal(first.Id, Assert.Single(_tracker.Active).Id);
    }

    [Fact]
    public void Starts_AsCriticalWhenCriticalForTheWholeRun()
    {
        var alert = CpuRun(95, 92, 91).Single().Alert;

        Assert.Equal(HealthLevel.Critical, alert.Severity);
        Assert.Equal(90, alert.Threshold);
    }

    [Fact]
    public void Starts_AsWarningWhenCriticalOnlyPartOfTheRun()
    {
        var alert = CpuRun(80, 95, 95).Single().Alert;

        Assert.Equal(HealthLevel.Warning, alert.Severity);
        Assert.Equal(75, alert.Threshold);
        Assert.Equal(95, alert.Value);   // the worst figure, even though it wasn't critical for long
    }

    [Fact]
    public void OneSample_StartsAndEndsOnTheFirstSample()
    {
        _tracker.Samples = 1;

        Assert.Equal(AlertChangeKind.Started, Assert.Single(Cpu(80)).Kind);
        var ended = Assert.Single(Cpu(10));

        Assert.Equal(AlertChangeKind.Ended, ended.Kind);
        Assert.Equal(At(1), ended.Alert.EndedUtc);
    }

    // ── While active ──────────────────────────────────────────────────────────

    [Fact]
    public void Escalates_OnceCriticalForTheSetNumberOfSamplesInARow()
    {
        var id = CpuRun(80, 80, 80).Single().Alert.Id;

        Assert.DoesNotContain(CpuRun(95, 95, 80, 95), c => c.Kind == AlertChangeKind.Escalated);   // two, then broken
        var escalated = CpuRun(95, 95).Single(c => c.Kind == AlertChangeKind.Escalated).Alert;

        Assert.Equal(id, escalated.Id);
        Assert.Equal(HealthLevel.Critical, escalated.Severity);
        Assert.Equal(90, escalated.Threshold);
        Assert.Equal(At(0), escalated.StartedUtc);
    }

    [Fact]
    public void StaysCritical_WhenTheFigureFallsBackToWarning()
    {
        CpuRun(95, 95, 95);

        Assert.Empty(CpuRun(80, 80, 80));
        Assert.Equal(HealthLevel.Critical, Assert.Single(_tracker.Active).Severity);
    }

    [Fact]
    public void Worsened_KeepsTheWorstFigureAndWhatTheDotSaidThen()
    {
        CpuRun(80, 85, 82);

        var worse = Assert.Single(Cpu(88));
        Assert.Equal(AlertChangeKind.Worsened, worse.Kind);
        Assert.Equal(88, worse.Alert.Value);
        Assert.Equal("CPU 88%", worse.Alert.Detail);

        Assert.Empty(Cpu(86));   // not worse: nothing to save
        Assert.Equal(88, Assert.Single(_tracker.Active).Value);
    }

    [Fact]
    public void Worsened_ForPageLifeExpectancyIsTheLowestFigure()
    {
        var started = new[] { 250.0, 200, 220 }.SelectMany(ple => Next(Quiet(ple: ple))).Single().Alert;
        Assert.Equal(200, started.Value);
        Assert.Equal("PLE 200s", started.Detail);

        Assert.Empty(Next(Quiet(ple: 280)));
        Assert.Equal(150, Next(Quiet(ple: 150)).Single().Alert.Value);
    }

    // ── Ending ────────────────────────────────────────────────────────────────

    [Fact]
    public void Ends_AfterTheSetNumberOfSamplesBackToNormalAtTheFirstOfThem()
    {
        CpuRun(80, 80, 80);

        Assert.Empty(CpuRun(10, 10));
        var ended = Assert.Single(Cpu(10));

        Assert.Equal(AlertChangeKind.Ended, ended.Kind);
        Assert.Equal(At(3), ended.Alert.EndedUtc);
        Assert.Equal(AlertEndReason.Recovered, ended.Alert.EndReason);
        Assert.False(ended.Alert.IsActive);
        Assert.Equal(TimeSpan.FromSeconds(30), ended.Alert.Duration(At(99)));
        Assert.Empty(_tracker.Active);
    }

    [Fact]
    public void Ends_AfterTheRunBackToNormalWhenItsThresholdIsTurnedOff()
    {
        CpuRun(80, 80, 80);
        var off = HealthThresholds.Default.With(HealthIndicator.Cpu, new HealthThreshold(null, null));

        Assert.Empty(_tracker.Evaluate(HealthRules.Grade(Quiet(cpuPct: 80), off), At(_sample++)));
        Assert.Empty(_tracker.Evaluate(HealthRules.Grade(Quiet(cpuPct: 80), off), At(_sample++)));
        Assert.Equal(AlertChangeKind.Ended,
                     Assert.Single(_tracker.Evaluate(HealthRules.Grade(Quiet(cpuPct: 80), off), At(_sample++))).Kind);
    }

    [Fact]
    public void ANewBreachAfterItEnded_StartsANewAlert()
    {
        var first = CpuRun(80, 80, 80).Single().Alert;
        CpuRun(10, 10, 10);

        var second = CpuRun(80, 80, 80).Single().Alert;

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(At(6), second.StartedUtc);
    }

    [Fact]
    public void AnIndicatorWithNoFigure_CountsAsBackToNormal()
    {
        // Blocking is measured only while something is blocked.
        foreach (var _ in Enumerable.Range(0, 3))
            Next(Quiet(blocked: 2, blockSec: 45));
        var active = Assert.Single(_tracker.Active);
        Assert.Equal(HealthIndicator.Blocking, active.Indicator);
        Assert.Equal("Blocking 45s (2 blocked)", active.Detail);

        var changes = Enumerable.Range(0, 3).SelectMany(_ => Next(Quiet())).ToList();

        Assert.Equal(AlertEndReason.Recovered, Assert.Single(changes).Alert.EndReason);
    }

    // ── Indicators, settings, stopping ────────────────────────────────────────

    [Fact]
    public void Indicators_AreTrackedSeparately()
    {
        Next(Quiet(cpuPct: 80, ple: 200));
        Next(Quiet(cpuPct: 80, ple: 200));
        var started = Next(Quiet(cpuPct: 10, ple: 200));   // CPU's run broken, PLE's not

        Assert.Equal(HealthIndicator.PageLifeExpectancy, Assert.Single(started).Alert.Indicator);
    }

    [Fact]
    public void Samples_AChangeAppliesFromTheNextSample()
    {
        CpuRun(80, 80);
        _tracker.Samples = 5;

        Assert.Empty(CpuRun(80, 80));
        Assert.Single(Cpu(80));
    }

    [Fact]
    public void Samples_MustBeAtLeastOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _tracker.Samples = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertTracker(ConnectionId, samples: 0));
    }

    [Fact]
    public void Stop_EndsActiveAlertsAtTheLastSampleAndForgetsRunsInProgress()
    {
        CpuRun(80, 80, 80);
        Next(Quiet(cpuPct: 80, ple: 200));   // PLE's run has begun, but no alert yet

        var ended = Assert.Single(_tracker.Stop());

        Assert.Equal(AlertChangeKind.Ended, ended.Kind);
        Assert.Equal(AlertEndReason.MonitoringStopped, ended.Alert.EndReason);
        Assert.Equal(At(3), ended.Alert.EndedUtc);
        Assert.Empty(_tracker.Active);

        // Resumed: a run has to start over, for both indicators.
        Assert.Empty(CpuRun(80, 80));
        Assert.Empty(_tracker.Stop());
    }

    [Fact]
    public void Stop_WithNothingActiveChangesNothing()
    {
        Assert.Empty(_tracker.Stop());
        CpuRun(10, 80);
        Assert.Empty(_tracker.Stop());
    }
}
