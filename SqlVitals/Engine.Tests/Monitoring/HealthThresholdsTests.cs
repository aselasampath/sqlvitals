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
    public void Grade_AFigureTheServerDidntReportIsUnknown_NotHealthy()
    {
        // PLE, TempDB and the log not reported (#173): grey, never passing.
        var readings = HealthRules.Grade(Sample(ple: 0, tempDbPct: 0, logPct: 0, logDb: null), HealthThresholds.Default);

        Assert.Equal([HealthIndicator.PageLifeExpectancy, HealthIndicator.LogSpace, HealthIndicator.TempDbSpace],
                     readings.Where(r => r.Level == HealthLevel.Unknown).Select(r => r.Indicator));
        Assert.All(readings.Where(r => r.Level == HealthLevel.Unknown), r => Assert.False(r.Measured));
        Assert.Equal(HealthLevel.Healthy, HealthRules.Summarize(readings).Level);
    }

    [Fact]
    public void Grade_NothingBlockedIsAFigure_AndAWaitWithoutABlockedRequestIsIgnored()
    {
        var blocking = HealthRules.Grade(Sample(blocked: 0, blockSec: 500), HealthThresholds.Default)[1];

        Assert.Equal((HealthIndicator.Blocking, HealthLevel.Healthy, 0.0, true), (blocking.Indicator, blocking.Level, blocking.Value, blocking.Measured));
    }

    // ── Page life expectancy per 4 GB of buffer pool (#173) ───────────────────

    [Theory]
    [InlineData(64 * 1024, 3000, 4800, HealthLevel.Warning)]  // 64 GB: 300 × 64 ÷ 4
    [InlineData(2 * 1024,  200,  150,  HealthLevel.Healthy)]  // a small VM during CHECKDB
    [InlineData(4 * 1024,  250,  300,  HealthLevel.Warning)]  // 4 GB: the classic 300 s
    [InlineData(0,         250,  300,  HealthLevel.Warning)]  // buffer pool not reported: as typed
    public void Ple_ScalesWithTheBufferPoolByDefault(double bufferPoolMb, double ple, double expectedWarning, HealthLevel expected)
    {
        var reading = HealthRules.Grade(Sample(ple: ple) with { BufferPoolMb = bufferPoolMb }, HealthThresholds.Default)[2];

        Assert.Equal(expectedWarning, reading.Limits.Warning);
        Assert.Null(reading.Limits.Critical);
        Assert.Equal(expected, reading.Level);
    }

    [Fact]
    public void Ple_FixedUsesTheSecondsAsTheyAre()
    {
        var fixedPle = HealthThresholds.Default with { PageLifeExpectancyFixed = true };

        var reading = HealthRules.Grade(Sample(ple: 3000) with { BufferPoolMb = 64 * 1024 }, fixedPle)[2];

        Assert.Equal((HealthLevel.Healthy, new HealthThreshold(300, null)), (reading.Level, reading.Limits));
        Assert.Null(reading.Note);
    }

    [Fact]
    public void Ple_ScalesBothLevels_AndSaysWhatItScaledTo()
    {
        var thresholds = HealthThresholds.Default.With(HealthIndicator.PageLifeExpectancy, new HealthThreshold(300, 150));

        var reading = InCulture("en-GB", () => HealthRules.Grade(Sample(ple: 1000) with { BufferPoolMb = 32 * 1024 }, thresholds)[2]);

        Assert.Equal((new HealthThreshold(2400, 1200), HealthLevel.Critical), (reading.Limits, reading.Level));
        Assert.Equal("Thresholds scaled to its 32 GB buffer pool: 300 / 150 s per 4 GB.", reading.Note);
    }

    // ── Log space and log_reuse_wait_desc (#173) ──────────────────────────────

    [Theory]
    [InlineData(200,  "NOTHING",            HealthLevel.Healthy)]   // small, reusable: sized tight
    [InlineData(200,  "CHECKPOINT",         HealthLevel.Healthy)]   // reusable at the next checkpoint
    [InlineData(200,  "LOG_BACKUP",         HealthLevel.Critical)]  // waiting on a log backup
    [InlineData(200,  "ACTIVE_TRANSACTION", HealthLevel.Critical)]
    [InlineData(4096, "NOTHING",            HealthLevel.Critical)]  // reusable, but not small
    [InlineData(200,  null,                 HealthLevel.Critical)]  // reuse wait not reported
    [InlineData(0,    "NOTHING",            HealthLevel.Critical)]  // size not reported
    public void Log_ASmallLogThatIsFullButReusable_IsntAWarning(double sizeMb, string? wait, HealthLevel expected)
    {
        var sample  = Sample(logPct: 95) with { LogSizeMb = sizeMb, LogReuseWait = wait };
        var reading = HealthRules.Grade(sample, HealthThresholds.Default)[4];

        Assert.Equal(expected, reading.Level);
        Assert.Equal(expected == HealthLevel.Healthy, reading.Note is not null);
    }

    [Fact]
    public void Log_ReasonAndNoteNameTheDatabaseAndItsReuseWait()
    {
        var waiting = HealthRules.Grade(Sample(logPct: 95) with { LogSizeMb = 200, LogReuseWait = "LOG_BACKUP" }, HealthThresholds.Default)[4];
        var spared  = InCulture("en-GB", () =>
            HealthRules.Grade(Sample(logPct: 95) with { LogSizeMb = 200, LogReuseWait = "NOTHING" }, HealthThresholds.Default)[4]);

        Assert.Equal("Log 95% full (Sales, waiting on LOG_BACKUP)", waiting.Reason);
        Assert.Null(spared.Reason);
        Assert.Equal("Not a warning: the log of Sales is only 200 MB, and SQL Server can reuse it (NOTHING).", spared.Note);
    }

    [Fact]
    public void Regrade_GradesTheFigureAgainWithTheSameFacts()
    {
        var reading = HealthRules.Grade(Sample(ple: 3000) with { BufferPoolMb = 64 * 1024 }, HealthThresholds.Default)[2];

        var regraded = HealthRules.Regrade(reading, HealthThresholds.Default with { PageLifeExpectancyFixed = true });

        Assert.Equal((HealthLevel.Warning, HealthLevel.Healthy), (reading.Level, regraded.Level));
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
        Assert.True(read.ScalesPageLifeExpectancy);
    }

    [Fact]
    public void Json_WeightsAndPleScalingRoundTrip()
    {
        var options  = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var original = HealthThresholds.Default with
        {
            Weights                 = HealthWeights.Default.With(HealthIndicator.Cpu, 0).With(HealthIndicator.TempDbSpace, 30),
            PageLifeExpectancyFixed = true,
        };

        var read = HealthThresholds.Normalize(JsonSerializer.Deserialize<HealthThresholds>(JsonSerializer.Serialize(original, options), options));

        Assert.Equal(original, read);
        Assert.Equal((0, 30, 20), (read.Weight(HealthIndicator.Cpu), read.Weight(HealthIndicator.TempDbSpace), read.Weight(HealthIndicator.Blocking)));
    }

    [Fact]
    public void Normalize_AFileFromBefore173_ScalesPle_UnlessItsThresholdHadBeenChanged()
    {
        // Saved before #173: no weights, and nothing about how PLE is judged.
        const string untouched = "{\"CpuPct\":{\"Warning\":75,\"Critical\":90},\"PageLifeExpectancySec\":{\"Warning\":300}}";
        const string changed   = "{\"PageLifeExpectancySec\":{\"Warning\":1000}}";

        var scaled  = HealthThresholds.Normalize(JsonSerializer.Deserialize<HealthThresholds>(untouched));
        var kept    = HealthThresholds.Normalize(JsonSerializer.Deserialize<HealthThresholds>(changed));

        Assert.Equal((false, HealthWeights.Default), (scaled.PageLifeExpectancyFixed!.Value, scaled.Weights));
        Assert.Equal((true, 1000.0), (kept.PageLifeExpectancyFixed!.Value, kept.Get(HealthIndicator.PageLifeExpectancy).Warning!.Value));
    }

    // ── Weights (#173) ────────────────────────────────────────────────────────

    [Fact]
    public void Weights_DefaultsStayAsTheyWere()
    {
        Assert.Equal(new HealthWeights(20, 20, 16, 12, 20, 12), HealthWeights.Default);
        Assert.Equal(HealthWeights.Default, HealthThresholds.Default.Weights);
    }

    [Fact]
    public void Weights_NormalizeFixesOutOfRange_AndAllZeroGivesTheDefaults()
    {
        var fixedUp = HealthWeights.Normalize(new HealthWeights(Cpu: 0, Blocking: 101, TempDbSpace: -1));
        Assert.Equal(new HealthWeights(0, 20, 16, 12, 20, 12), fixedUp);

        Assert.Equal(HealthWeights.Default, HealthWeights.Normalize(new HealthWeights(0, 0, 0, 0, 0, 0)));
        Assert.Equal(HealthWeights.Default, HealthWeights.Normalize(null));
    }

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData(" 35 ", true, 35)]
    [InlineData("100", true, 100)]
    [InlineData("101", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("2.5", false, 0)]
    [InlineData("", false, 0)]
    public void Weights_TryParseAcceptsWholeNumbersFrom0To100(string text, bool ok, int expected)
    {
        var parsed = InCulture("en-GB", () => (HealthWeights.TryParse(HealthIndicator.Cpu, text, out var weight, out var error), weight, error));

        Assert.Equal(ok, parsed.Item1);
        if (ok)
            Assert.Equal(expected, parsed.weight);
        else
            Assert.Equal("CPU: enter a weight from 0 to 100; 0 leaves it out of the score.", parsed.error);
    }
}
