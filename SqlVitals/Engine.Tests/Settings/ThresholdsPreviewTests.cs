using System.Globalization;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Settings;
using SqlVitals.Engine.Tests.Home;

namespace SqlVitals.Engine.Tests.Settings;

/// <summary>The preview on a connection's Health thresholds tab (#131): its latest sample on the thresholds being edited.</summary>
public sealed class ThresholdsPreviewTests
{
    private static readonly HealthThresholds Defaults = HealthThresholds.Default;

    // CPU at 81 % (warning at 75) and 3 minutes of blocking (critical at 2): 100 − 10 − 20.
    private static readonly HealthScore Uat = ServerTileTests.Score(ServerTileTests.Sample(cpuPct: 81, blocked: 3, blockSec: 180));

    [Fact]
    public void ACheckTurnedFromWarningToHealthy_GetsItsPointsBack()
    {
        var edited  = Defaults.With(HealthIndicator.Cpu, new HealthThreshold(85, 95));
        var preview = ThresholdsPreview.For(Uat, edited)!;

        Assert.Equal(70, preview.Before);
        Assert.Equal(80, preview.After);
        var cpu = preview.Regraded.Checks.Single(c => c.Indicator == HealthIndicator.Cpu);
        Assert.Equal((HealthLevel.Healthy, 0), (cpu.Level, cpu.PointsLost));
        var blocking = preview.Regraded.Checks.Single(c => c.Indicator == HealthIndicator.Blocking);
        Assert.Equal((HealthLevel.Critical, 20), (blocking.Level, blocking.PointsLost));
        Assert.Equal(HealthLevel.Critical, preview.Regraded.Level);
        Assert.Equal(new HealthThreshold(85, 95), cpu.Reading.Limits);
    }

    [Fact]
    public void AWeightChanged_PreviewsTheScoreOnTheNewWeights()
    {
        // Blocking weighed 0: CPU's warning is half of its 25 (20 × 100 ÷ 80).
        var edited  = Defaults with { Weights = HealthWeights.Default.With(HealthIndicator.Blocking, 0) };
        var preview = ThresholdsPreview.For(Uat, edited)!;

        Assert.Equal((70, 88), (preview.Before, preview.After));
        Assert.Equal(HealthLevel.Critical, preview.Regraded.Level);
        Assert.Equal((0, 0), (preview.Regraded.Checks[1].Worth, preview.Regraded.Checks[1].PointsLost));
    }

    [Fact]
    public void PleTurnedToFixed_PreviewsTheSampleOnTheFixedThreshold()
    {
        // 3,000 s on a 64 GB buffer pool: past 4,800 s scaled, fine against a fixed 300 s.
        var latest  = ServerTileTests.Score(ServerTileTests.Sample(ple: 3000) with { BufferPoolMb = 64 * 1024 });
        var preview = ThresholdsPreview.For(latest, Defaults with { PageLifeExpectancyFixed = true })!;

        Assert.Equal((92, 100), (preview.Before, preview.After));
    }

    [Fact]
    public void ACheckWithNoData_StaysUnknownInThePreview()
    {
        var latest  = ServerTileTests.Score(ServerTileTests.Sample(tempDbPct: 0));
        var preview = ThresholdsPreview.For(latest, Defaults.With(HealthIndicator.TempDbSpace, new HealthThreshold(1, 2)))!;

        Assert.Equal(HealthLevel.Unknown, preview.Regraded.Checks.Single(c => c.Indicator == HealthIndicator.TempDbSpace).Level);
        Assert.Equal(100, preview.After);
    }

    [Fact]
    public void Text_SaysTheScoreBeforeAndAfter_OrThatItStaysTheSame()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        try
        {
            var changed   = ThresholdsPreview.For(Uat, Defaults.With(HealthIndicator.Cpu, new HealthThreshold(85, 95)))!;
            var unchanged = ThresholdsPreview.For(Uat, Defaults)!;

            Assert.Equal("On the latest sample, UAT would score 70 → 80", changed.Text("UAT"));
            Assert.Equal("On the latest sample, UAT would still score 70", unchanged.Text("UAT"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Regrading_OnTheThresholdsItWasGradedOn_GivesTheSameScore_AsTheDot()
    {
        var sample = ServerTileTests.Sample(cpuPct: 95, ple: 120, logPct: 85, tempDbPct: 91);
        var own    = Defaults.With(HealthIndicator.LogSpace, new HealthThreshold(70, 80));
        var graded = HealthScore.From(HealthRules.Grade(sample, own), DateTime.Now);

        // From the default grading, regraded on the connection's own: as if graded on them in the first place.
        var regraded = ThresholdsPreview.Regrade(ServerTileTests.Score(sample), own);

        Assert.Equal(graded.Value, regraded.Value);
        Assert.Equal(graded.Level, regraded.Level);
        Assert.Equal(graded.Checks.Select(c => (c.Indicator, c.Level, c.PointsLost)),
                     regraded.Checks.Select(c => (c.Indicator, c.Level, c.PointsLost)));
    }

    [Fact]
    public void AFigureTheSampleDidntHave_StaysHealthy_WhateverTheThreshold()
    {
        // Nothing blocked: the blocking figure isn't measured, so no threshold can make it count.
        var quiet   = ServerTileTests.Score(ServerTileTests.Sample());
        var preview = ThresholdsPreview.For(quiet, Defaults.With(HealthIndicator.Blocking, new HealthThreshold(1, 2)))!;

        Assert.Equal(100, preview.After);
        Assert.Equal(HealthLevel.Healthy, preview.Regraded.Checks.Single(c => c.Indicator == HealthIndicator.Blocking).Level);
    }

    [Fact]
    public void WithNoSample_OrAServerThatCantBeReached_ThereIsNoPreview()
    {
        Assert.Null(ThresholdsPreview.For(null, Defaults));
        Assert.Null(ThresholdsPreview.For(HealthScore.Unavailable("Login failed.", DateTime.Now), Defaults));
    }
}
