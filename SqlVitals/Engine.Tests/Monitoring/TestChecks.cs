using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Monitoring;

/// <summary>
/// The slow checks (#174) as a healthy server reports them, so a test can grade a sample the way a
/// collector does: its six checks and these three.
/// </summary>
internal static class TestChecks
{
    private static readonly DateTime ReadUtc = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Half of a 1 TB disk free, no database past its RPO, no job failed.</summary>
    public static ServerCheckFigures HealthyFigures { get; } =
        new([new VolumeSpace(@"D:\", 1000, 500)], null, [], null, [], null, LastJobInstanceId: 10);

    public static IReadOnlyList<HealthReading> Readings(ServerCheckFigures figures, HealthThresholds? thresholds = null)
    {
        var checks = new ServerChecks();
        checks.Apply(figures, ReadUtc);
        return checks.Readings(thresholds ?? HealthThresholds.Default, ReadUtc);
    }

    public static IReadOnlyList<HealthReading> Healthy(HealthThresholds? thresholds = null) => Readings(HealthyFigures, thresholds);

    /// <summary>A sample's six checks and the three slow ones, healthy unless given.</summary>
    public static IReadOnlyList<HealthReading> All(LiveMetricSample sample, HealthThresholds? thresholds = null,
                                                   IReadOnlyList<HealthReading>? slow = null) =>
        [.. HealthRules.Grade(sample, thresholds ?? HealthThresholds.Default), .. slow ?? Healthy(thresholds)];
}
