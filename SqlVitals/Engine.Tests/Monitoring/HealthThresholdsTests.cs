using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Monitoring;

public class HealthThresholdsTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    // A quiet server: nothing near any default threshold.
    private static LiveMetricSample Sample(
        double cpuPct = 10, double ple = 5000, double grants = 0,
        double blocked = 0, double blockSec = 0,
        double logPct = 5, string? logDb = "Sales", double tempDbPct = 10) =>
        new(T0, 0, 0, 0, 0, 0, 0, cpuPct, 0, 0, 0, 0, 0, 0, ple, grants, 99.9,
            blocked, blockSec, logPct, logDb, tempDbPct);

    private static HealthThresholds With(HealthIndicator indicator, double? warning, double? critical) =>
        HealthThresholds.Default.With(indicator, new HealthThreshold(warning, critical));

    private static T InCulture<T>(string name, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── Defaults ──────────────────────────────────────────────────────────────

    [Fact]
    public void Default_KeepsTheThresholdsThatUsedToBeFixedInCode()
    {
        var d = HealthThresholds.Default;

        Assert.Equal(new HealthThreshold(75, 90),    d.CpuPct);
        Assert.Equal(new HealthThreshold(300, null), d.PageLifeExpectancySec);
        Assert.Equal(new HealthThreshold(1, null),   d.MemoryGrantsPending);
    }

    [Fact]
    public void Default_CoversEveryIndicator()
    {
        Assert.Equal(Enum.GetValues<HealthIndicator>(), HealthThresholds.Indicators.Select(i => i.Indicator));
        foreach (var info in HealthThresholds.Indicators)
            Assert.Equal(info.Default, HealthThresholds.Default.Get(info.Indicator));
    }

    [Fact]
    public void Evaluate_QuietServerIsHealthy()
    {
        var (level, reasons) = HealthRules.Evaluate(Sample(), HealthThresholds.Default);

        Assert.Equal(HealthLevel.Healthy, level);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Grade_ReadsEveryIndicatorInOrderWithItsLimits()
    {
        var readings = HealthRules.Grade(Sample(cpuPct: 80, logPct: 95), HealthThresholds.Default);

        Assert.Equal(Enum.GetValues<HealthIndicator>(), readings.Select(r => r.Indicator));
        var cpu = readings[0];
        Assert.Equal((HealthLevel.Warning, 80.0, new HealthThreshold(75, 90), "CPU 80%"),
                     (cpu.Level, cpu.Value, cpu.Limits, cpu.Reason));
        Assert.Equal(HealthLevel.Critical, readings.Single(r => r.Indicator == HealthIndicator.LogSpace).Level);
        Assert.All(readings.Where(r => r.Level == HealthLevel.Healthy), r => Assert.Null(r.Reason));
    }

    [Fact]
    public void Grade_AnIndicatorWithoutAFigureIsHealthy()
    {
        // Nothing blocked, PLE and TempDB not reported: none of them can be past a threshold.
        var readings = HealthRules.Grade(Sample(ple: 0, blocked: 0, blockSec: 500, tempDbPct: 0), HealthThresholds.Default);

        Assert.All(readings, r => Assert.Equal(HealthLevel.Healthy, r.Level));
    }

    [Fact]
    public void Summarize_MatchesEvaluate()
    {
        var sample = Sample(cpuPct: 95, ple: 100, grants: 2);

        var (level, reasons) = HealthRules.Summarize(HealthRules.Grade(sample, HealthThresholds.Default));

        Assert.Equal(HealthLevel.Critical, level);
        Assert.Equal(HealthRules.Evaluate(sample).Reasons, reasons);
    }

    // ── Each indicator ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(84.9, HealthLevel.Healthy)]
    [InlineData(85,   HealthLevel.Warning)]
    [InlineData(95,   HealthLevel.Critical)]
    public void Evaluate_UsesCustomCpuThresholds(double cpuPct, HealthLevel expected)
    {
        var (level, _) = HealthRules.Evaluate(Sample(cpuPct: cpuPct), With(HealthIndicator.Cpu, 85, 95));

        Assert.Equal(expected, level);
    }

    [Fact]
    public void Evaluate_ABlankLevelIsOff()
    {
        var (warnOnly, _) = HealthRules.Evaluate(Sample(cpuPct: 99), With(HealthIndicator.Cpu, 75, null));
        var (off, reasons) = HealthRules.Evaluate(Sample(cpuPct: 99), With(HealthIndicator.Cpu, null, null));

        Assert.Equal(HealthLevel.Warning, warnOnly);
        Assert.Equal(HealthLevel.Healthy, off);
        Assert.Empty(reasons);
    }

    [Theory]
    [InlineData(0,  0,   HealthLevel.Healthy)]    // nothing blocked
    [InlineData(2,  29,  HealthLevel.Healthy)]
    [InlineData(2,  30,  HealthLevel.Warning)]
    [InlineData(1,  120, HealthLevel.Critical)]
    public void Evaluate_GradesTheLongestBlock(double blocked, double seconds, HealthLevel expected)
    {
        var (level, _) = HealthRules.Evaluate(Sample(blocked: blocked, blockSec: seconds), HealthThresholds.Default);

        Assert.Equal(expected, level);
    }

    [Fact]
    public void Evaluate_NamesTheBlockAndHowManyAreBlocked()
    {
        var (_, reasons) = InCulture("en-US", () =>
            HealthRules.Evaluate(Sample(blocked: 3, blockSec: 150), HealthThresholds.Default));

        Assert.Equal(new[] { "Blocking 2.5 min (3 blocked)" }, reasons);
    }

    [Theory]
    [InlineData(301, HealthLevel.Healthy)]
    [InlineData(299, HealthLevel.Warning)]
    [InlineData(59,  HealthLevel.Critical)]
    [InlineData(0,   HealthLevel.Healthy)]    // counter not reported
    public void Evaluate_PageLifeExpectancyIsWorseWhenLower(double ple, HealthLevel expected)
    {
        var (level, _) = HealthRules.Evaluate(Sample(ple: ple), With(HealthIndicator.PageLifeExpectancy, 300, 60));

        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(2, HealthLevel.Healthy)]
    [InlineData(3, HealthLevel.Warning)]
    [InlineData(10, HealthLevel.Critical)]
    public void Evaluate_MemoryGrantsPendingCanTolerateAFew(double grants, HealthLevel expected)
    {
        var (level, _) = HealthRules.Evaluate(Sample(grants: grants), With(HealthIndicator.MemoryGrantsPending, 3, 10));

        Assert.Equal(expected, level);
    }

    [Fact]
    public void Evaluate_NamesTheDatabaseWhoseLogIsFullest()
    {
        var (level, reasons) = HealthRules.Evaluate(Sample(logPct: 93, logDb: "Sales"), HealthThresholds.Default);

        Assert.Equal(HealthLevel.Critical, level);
        Assert.Equal(new[] { "Log 93% full (Sales)" }, reasons);
    }

    [Theory]
    [InlineData(79, HealthLevel.Healthy)]
    [InlineData(80, HealthLevel.Warning)]
    [InlineData(90, HealthLevel.Critical)]
    [InlineData(0,  HealthLevel.Healthy)]     // counters not reported (Azure SQL Database)
    public void Evaluate_GradesTempDbSpace(double pct, HealthLevel expected)
    {
        var (level, _) = HealthRules.Evaluate(Sample(tempDbPct: pct), HealthThresholds.Default);

        Assert.Equal(expected, level);
    }

    [Fact]
    public void Evaluate_KeepsTheWorstLevelAndEveryReason()
    {
        var (level, reasons) = HealthRules.Evaluate(
            Sample(cpuPct: 80, blocked: 1, blockSec: 45, logPct: 95, tempDbPct: 85), HealthThresholds.Default);

        Assert.Equal(HealthLevel.Critical, level);
        Assert.Equal(4, reasons.Count);
    }

    // ── Parsing what the user typed ───────────────────────────────────────────

    [Theory]
    [InlineData("75", "90", 75.0, 90.0)]
    [InlineData(" 80 % ", "", 80.0, null)]
    [InlineData("", "", null, null)]
    [InlineData("12.5", "30s", 12.5, 30.0)]
    public void TryParse_AcceptsNumbersAndBlanks(string warning, string critical, double? expectedWarning, double? expectedCritical)
    {
        var (ok, threshold, error) = InCulture("en-US", () =>
        {
            var ok = HealthThresholds.TryParse(HealthIndicator.Cpu, warning, critical, out var t, out var e);
            return (ok, t, e);
        });

        Assert.True(ok, error);
        Assert.Equal(new HealthThreshold(expectedWarning, expectedCritical), threshold);
    }

    [Theory]
    [InlineData(HealthIndicator.Cpu, "0", "")]                      // below the range
    [InlineData(HealthIndicator.Cpu, "101", "")]                    // a percentage over 100
    [InlineData(HealthIndicator.Cpu, "abc", "")]
    [InlineData(HealthIndicator.Cpu, "90", "75")]                   // critical must be higher
    [InlineData(HealthIndicator.Cpu, "90", "90")]
    [InlineData(HealthIndicator.PageLifeExpectancy, "300", "600")]  // critical must be lower
    [InlineData(HealthIndicator.MemoryGrantsPending, "1.5", "")]    // a whole number
    [InlineData(HealthIndicator.Blocking, "", "100000")]
    public void TryParse_RejectsWhatCantBeUsed(HealthIndicator indicator, string warning, string critical)
    {
        var (ok, threshold, error) = InCulture("en-US", () =>
        {
            var ok = HealthThresholds.TryParse(indicator, warning, critical, out var t, out var e);
            return (ok, t, e);
        });

        Assert.False(ok);
        Assert.Null(threshold);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParse_ErrorNamesTheIndicator()
    {
        HealthThresholds.TryParse(HealthIndicator.LogSpace, "95", "80", out var threshold, out var error);

        Assert.Null(threshold);
        Assert.StartsWith("Log space used:", error);
    }

    [Fact]
    public void Format_ShowsBlankForOffAndNoTrailingZeros()
    {
        Assert.Equal("", HealthThresholds.Format(null));
        Assert.Equal("75", InCulture("en-US", () => HealthThresholds.Format(75)));
        Assert.Equal("12.5", InCulture("en-US", () => HealthThresholds.Format(12.5)));
    }

    // ── Saved values ──────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_NullIsTheDefault() =>
        Assert.Equal(HealthThresholds.Default, HealthThresholds.Normalize(null));

    [Fact]
    public void Normalize_KeepsValidValuesAndFixesTheRest()
    {
        var saved = new HealthThresholds(
            CpuPct:                new HealthThreshold(60, 80),     // valid
            BlockingSec:           new HealthThreshold(null, null), // valid: both levels off
            PageLifeExpectancySec: new HealthThreshold(300, 900),   // wrong order
            MemoryGrantsPending:   new HealthThreshold(0.5, null),  // not a whole number
            LogUsedPct:            new HealthThreshold(150, null),  // out of range
            TempDbUsedPct:         null);                           // missing (older file)

        var result = HealthThresholds.Normalize(saved);

        Assert.Equal(new HealthThreshold(60, 80),     result.CpuPct);
        Assert.Equal(new HealthThreshold(null, null), result.BlockingSec);
        Assert.Equal(HealthThresholds.Default.PageLifeExpectancySec, result.PageLifeExpectancySec);
        Assert.Equal(HealthThresholds.Default.MemoryGrantsPending,   result.MemoryGrantsPending);
        Assert.Equal(HealthThresholds.Default.LogUsedPct,            result.LogUsedPct);
        Assert.Equal(HealthThresholds.Default.TempDbUsedPct,         result.TempDbUsedPct);
    }

    [Fact]
    public void Json_RoundTripsLikeTheSettingsFile()
    {
        // settings.dat leaves nulls out, so a level that is off is written as a missing value.
        var options  = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var original = With(HealthIndicator.Cpu, 60, null).With(HealthIndicator.Blocking, new HealthThreshold(null, null));

        var json = JsonSerializer.Serialize(original, options);
        var read = HealthThresholds.Normalize(JsonSerializer.Deserialize<HealthThresholds>(json, options));

        Assert.Equal(original, read);
    }

    [Fact]
    public void Json_AnEmptyObjectGivesTheDefaults()
    {
        var read = HealthThresholds.Normalize(JsonSerializer.Deserialize<HealthThresholds>("{}"));

        Assert.Equal(HealthThresholds.Default, read);
    }
}
