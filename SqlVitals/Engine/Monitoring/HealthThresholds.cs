using System.Globalization;

namespace SqlVitals.Engine.Monitoring;

/// <summary>The figures the health dot is graded on.</summary>
public enum HealthIndicator
{
    Cpu,
    Blocking,
    PageLifeExpectancy,
    MemoryGrantsPending,
    LogSpace,
    TempDbSpace,
}

/// <summary>
/// When one indicator turns the dot amber (<see cref="Warning"/>) or red (<see cref="Critical"/>).
/// Null turns that level off for the indicator.
/// </summary>
public sealed record HealthThreshold(double? Warning, double? Critical);

/// <summary>What an indicator measures and which values it accepts; drives Settings and the rules.</summary>
public sealed record HealthIndicatorInfo(
    HealthIndicator Indicator,
    string          Name,
    string          Unit,
    string          Hint,
    bool            LowerIsWorse,
    bool            WholeNumber,
    double          Min,
    double          Max,
    HealthThreshold Default);

/// <summary>
/// The traffic-light thresholds for each <see cref="HealthIndicator"/>. Saved in Settings for
/// every connection, and optionally overridden for one connection.
/// </summary>
public sealed record HealthThresholds(
    HealthThreshold? CpuPct,
    HealthThreshold? BlockingSec,
    HealthThreshold? PageLifeExpectancySec,
    HealthThreshold? MemoryGrantsPending,
    HealthThreshold? LogUsedPct,
    HealthThreshold? TempDbUsedPct)
{
    public static IReadOnlyList<HealthIndicatorInfo> Indicators { get; } =
    [
        new(HealthIndicator.Cpu, "CPU", "%",
            "SQL Server's share of the CPU over the last minute.",
            LowerIsWorse: false, WholeNumber: false, Min: 1, Max: 100, new(75, 90)),
        new(HealthIndicator.Blocking, "Blocking", "s",
            "How long the longest-blocked request has been waiting on another session.",
            LowerIsWorse: false, WholeNumber: false, Min: 1, Max: 86_400, new(30, 120)),
        new(HealthIndicator.PageLifeExpectancy, "Page life expectancy", "s",
            "Flags a value below the threshold: pages leaving the buffer pool quickly mean memory pressure.",
            LowerIsWorse: true, WholeNumber: false, Min: 1, Max: 1_000_000, new(300, null)),
        new(HealthIndicator.MemoryGrantsPending, "Memory grants pending", "",
            "Queries waiting for memory to start running.",
            LowerIsWorse: false, WholeNumber: true, Min: 1, Max: 10_000, new(1, null)),
        new(HealthIndicator.LogSpace, "Log space used", "%",
            "The fullest transaction log on the server, as a share of its current size.",
            LowerIsWorse: false, WholeNumber: false, Min: 1, Max: 100, new(80, 90)),
        new(HealthIndicator.TempDbSpace, "TempDB space used", "%",
            "TempDB data files, as a share of their current size (before they autogrow).",
            LowerIsWorse: false, WholeNumber: false, Min: 1, Max: 100, new(80, 90)),
    ];

    public static HealthThresholds Default { get; } = new(
        Info(HealthIndicator.Cpu).Default,
        Info(HealthIndicator.Blocking).Default,
        Info(HealthIndicator.PageLifeExpectancy).Default,
        Info(HealthIndicator.MemoryGrantsPending).Default,
        Info(HealthIndicator.LogSpace).Default,
        Info(HealthIndicator.TempDbSpace).Default);

    public static HealthIndicatorInfo Info(HealthIndicator indicator) =>
        Indicators.First(i => i.Indicator == indicator);

    /// <summary>The threshold for one indicator; the default when it is missing.</summary>
    public HealthThreshold Get(HealthIndicator indicator) => indicator switch
    {
        HealthIndicator.Cpu                 => CpuPct,
        HealthIndicator.Blocking            => BlockingSec,
        HealthIndicator.PageLifeExpectancy  => PageLifeExpectancySec,
        HealthIndicator.MemoryGrantsPending => MemoryGrantsPending,
        HealthIndicator.LogSpace            => LogUsedPct,
        HealthIndicator.TempDbSpace         => TempDbUsedPct,
        _                                   => null,
    } ?? Info(indicator).Default;

    public HealthThresholds With(HealthIndicator indicator, HealthThreshold threshold) => indicator switch
    {
        HealthIndicator.Cpu                 => this with { CpuPct                = threshold },
        HealthIndicator.Blocking            => this with { BlockingSec           = threshold },
        HealthIndicator.PageLifeExpectancy  => this with { PageLifeExpectancySec = threshold },
        HealthIndicator.MemoryGrantsPending => this with { MemoryGrantsPending   = threshold },
        HealthIndicator.LogSpace            => this with { LogUsedPct            = threshold },
        HealthIndicator.TempDbSpace         => this with { TempDbUsedPct         = threshold },
        _                                   => this,
    };

    /// <summary>
    /// Saved thresholds made safe to use: a missing indicator (from an older file) or one that
    /// is out of range or in the wrong order gets its default. Null gives <see cref="Default"/>.
    /// </summary>
    public static HealthThresholds Normalize(HealthThresholds? saved)
    {
        var result = Default;
        if (saved is null)
            return result;

        foreach (var info in Indicators)
        {
            var threshold = saved.Get(info.Indicator);
            result = result.With(info.Indicator, Validate(info, threshold) is null ? threshold : info.Default);
        }
        return result;
    }

    /// <summary>
    /// Reads what the user typed for one indicator: blank turns that level off. Returns false with
    /// a message to show when it can't be used.
    /// </summary>
    public static bool TryParse(HealthIndicator indicator, string warningText, string criticalText,
                                out HealthThreshold? threshold, out string error)
    {
        threshold = null;
        var info = Info(indicator);

        if (!TryParseValue(info, warningText, out var warning) || !TryParseValue(info, criticalText, out var critical))
        {
            error = RangeError(info);
            return false;
        }

        var candidate = new HealthThreshold(warning, critical);
        if (Validate(info, candidate) is { } problem)
        {
            error = problem;
            return false;
        }

        threshold = candidate;
        error     = string.Empty;
        return true;
    }

    /// <summary>How a threshold value is shown in a text box: blank when off, no trailing zeros.</summary>
    public static string Format(double? value) =>
        value is { } v ? v.ToString("0.##", CultureInfo.CurrentCulture) : string.Empty;

    private static bool TryParseValue(HealthIndicatorInfo info, string text, out double? value)
    {
        value = null;
        var trimmed = (text ?? string.Empty).Trim().TrimEnd('%', 's').Trim();
        if (trimmed.Length == 0)
            return true;

        if (!double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var v))
            return false;

        value = v;
        return InRange(info, v);
    }

    private static bool InRange(HealthIndicatorInfo info, double value) =>
        double.IsFinite(value) && value >= info.Min && value <= info.Max &&
        (!info.WholeNumber || value == Math.Floor(value));

    // Null when the threshold can be used, otherwise why not.
    private static string? Validate(HealthIndicatorInfo info, HealthThreshold threshold)
    {
        if (threshold.Warning is { } w && !InRange(info, w) || threshold.Critical is { } c && !InRange(info, c))
            return RangeError(info);

        if (threshold is { Warning: { } warning, Critical: { } critical })
        {
            if (info.LowerIsWorse && critical >= warning)
                return $"{info.Name}: the critical threshold must be lower than the warning one.";
            if (!info.LowerIsWorse && critical <= warning)
                return $"{info.Name}: the critical threshold must be higher than the warning one.";
        }
        return null;
    }

    private static string RangeError(HealthIndicatorInfo info) => string.Format(
        CultureInfo.CurrentCulture,
        info.WholeNumber
            ? "{0}: enter a whole number from {1:N0} to {2:N0}, or leave it blank to turn that level off."
            : "{0}: enter a number from {1:N0} to {2:N0}, or leave it blank to turn that level off.",
        info.Name, info.Min, info.Max);
}
