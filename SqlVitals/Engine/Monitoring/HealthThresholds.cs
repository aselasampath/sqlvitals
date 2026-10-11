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
/// What each check is worth in the health score (#173). Weights are relative: the score is always
/// out of 100, scaled over the checks that reported. 0 leaves a check out of the score; its
/// thresholds still colour the dot and raise alerts. Null takes the default.
/// </summary>
public sealed record HealthWeights(
    int? Cpu                 = null,
    int? Blocking            = null,
    int? PageLifeExpectancy  = null,
    int? MemoryGrantsPending = null,
    int? LogSpace            = null,
    int? TempDbSpace         = null)
{
    public const int MaxWeight = 100;

    public static HealthWeights Default { get; } = new(
        DefaultOf(HealthIndicator.Cpu), DefaultOf(HealthIndicator.Blocking), DefaultOf(HealthIndicator.PageLifeExpectancy),
        DefaultOf(HealthIndicator.MemoryGrantsPending), DefaultOf(HealthIndicator.LogSpace), DefaultOf(HealthIndicator.TempDbSpace));

    /// <summary>
    /// The default points of a check; they add up to 100. CPU, blocking and a full log stop users'
    /// work outright; memory pressure slows everything; queued memory grants and a full TempDB hit
    /// fewer queries.
    /// </summary>
    public static int DefaultOf(HealthIndicator indicator) => indicator switch
    {
        HealthIndicator.Cpu                 => 20,
        HealthIndicator.Blocking            => 20,
        HealthIndicator.LogSpace            => 20,
        HealthIndicator.PageLifeExpectancy  => 16,
        HealthIndicator.MemoryGrantsPending => 12,
        HealthIndicator.TempDbSpace         => 12,
        _                                   => 0,
    };

    /// <summary>One check's weight; the default when it is missing.</summary>
    public int Get(HealthIndicator indicator) => indicator switch
    {
        HealthIndicator.Cpu                 => Cpu,
        HealthIndicator.Blocking            => Blocking,
        HealthIndicator.PageLifeExpectancy  => PageLifeExpectancy,
        HealthIndicator.MemoryGrantsPending => MemoryGrantsPending,
        HealthIndicator.LogSpace            => LogSpace,
        HealthIndicator.TempDbSpace         => TempDbSpace,
        _                                   => null,
    } ?? DefaultOf(indicator);

    public HealthWeights With(HealthIndicator indicator, int weight) => indicator switch
    {
        HealthIndicator.Cpu                 => this with { Cpu                 = weight },
        HealthIndicator.Blocking            => this with { Blocking            = weight },
        HealthIndicator.PageLifeExpectancy  => this with { PageLifeExpectancy  = weight },
        HealthIndicator.MemoryGrantsPending => this with { MemoryGrantsPending = weight },
        HealthIndicator.LogSpace            => this with { LogSpace            = weight },
        HealthIndicator.TempDbSpace         => this with { TempDbSpace         = weight },
        _                                   => this,
    };

    /// <summary>
    /// Saved weights made safe to use: one missing or out of range gets its default, and all of
    /// them 0, which would leave nothing to score, gives <see cref="Default"/>.
    /// </summary>
    public static HealthWeights Normalize(HealthWeights? saved)
    {
        if (saved is null)
            return Default;

        var result = Default;
        foreach (var indicator in Enum.GetValues<HealthIndicator>())
        {
            var weight = saved.Get(indicator);
            result = result.With(indicator, weight is >= 0 and <= MaxWeight ? weight : DefaultOf(indicator));
        }
        return Enum.GetValues<HealthIndicator>().All(i => result.Get(i) == 0) ? Default : result;
    }

    /// <summary>Reads a typed weight: a whole number from 0 to 100. False with a message to show otherwise.</summary>
    public static bool TryParse(HealthIndicator indicator, string text, out int weight, out string error)
    {
        if (int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out weight) &&
            weight is >= 0 and <= MaxWeight)
        {
            error = string.Empty;
            return true;
        }
        error = $"{HealthThresholds.Info(indicator).Name}: enter a weight from 0 to {MaxWeight}; 0 leaves it out of the score.";
        return false;
    }
}

/// <summary>
/// The traffic-light thresholds for each <see cref="HealthIndicator"/>, the score's weights and
/// how page life expectancy is judged. Saved in Settings for every connection, and optionally
/// overridden for one connection.
/// </summary>
/// <param name="Weights">What each check is worth in the score (#173); null takes the defaults.</param>
/// <param name="PageLifeExpectancyFixed">
/// False (the default) reads the page life expectancy thresholds as seconds per 4 GB of buffer
/// pool, so a large server needs a longer one (#173); true uses them as they are. Null, from a file
/// saved before #173, is read by <see cref="Normalize"/>.
/// </param>
public sealed record HealthThresholds(
    HealthThreshold? CpuPct,
    HealthThreshold? BlockingSec,
    HealthThreshold? PageLifeExpectancySec,
    HealthThreshold? MemoryGrantsPending,
    HealthThreshold? LogUsedPct,
    HealthThreshold? TempDbUsedPct,
    HealthWeights?   Weights                 = null,
    bool?            PageLifeExpectancyFixed = null)
{
    /// <summary>The buffer pool size a scaled page life expectancy threshold is given for: 300 s per 4 GB.</summary>
    public const double PageLifeExpectancyPerGb = 4;

    public static IReadOnlyList<HealthIndicatorInfo> Indicators { get; } =
    [
        new(HealthIndicator.Cpu, "CPU", "%",
            "SQL Server's share of the CPU over the last minute.",
            LowerIsWorse: false, WholeNumber: false, Min: 1, Max: 100, new(75, 90)),
        new(HealthIndicator.Blocking, "Blocking", "s",
            "How long the longest-blocked request has been waiting on another session.",
            LowerIsWorse: false, WholeNumber: false, Min: 1, Max: 86_400, new(30, 120)),
        new(HealthIndicator.PageLifeExpectancy, "Page life expectancy", "s",
            "Flags a value below the threshold: pages leaving the buffer pool quickly mean memory pressure. " +
            "By default the threshold is per 4 GB of buffer pool, so a large server needs a longer one.",
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
        Info(HealthIndicator.TempDbSpace).Default,
        HealthWeights.Default,
        PageLifeExpectancyFixed: false);

    public static HealthIndicatorInfo Info(HealthIndicator indicator) =>
        Indicators.First(i => i.Indicator == indicator);

    /// <summary>A check's weight in the score.</summary>
    public int Weight(HealthIndicator indicator) => (Weights ?? HealthWeights.Default).Get(indicator);

    /// <summary>Page life expectancy's thresholds are per 4 GB of buffer pool (the default), not fixed.</summary>
    public bool ScalesPageLifeExpectancy => PageLifeExpectancyFixed != true;

    /// <summary>
    /// The threshold one figure is graded on: <see cref="Get"/>, except that a scaled page life
    /// expectancy threshold is multiplied by the buffer pool's size over 4 GB (300 s × GB ÷ 4),
    /// to whole seconds. With no buffer pool size (not reported) it is used as it is.
    /// </summary>
    public HealthThreshold For(HealthIndicator indicator, double bufferPoolGb)
    {
        var threshold = Get(indicator);
        if (indicator != HealthIndicator.PageLifeExpectancy || !ScalesPageLifeExpectancy || bufferPoolGb <= 0)
            return threshold;

        double? Scale(double? limit) =>
            limit is { } l ? Math.Max(1, Math.Round(l * bufferPoolGb / PageLifeExpectancyPerGb, MidpointRounding.AwayFromZero)) : null;
        return new HealthThreshold(Scale(threshold.Warning), Scale(threshold.Critical));
    }

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
    /// is out of range or in the wrong order gets its default, and so does a weight. Null gives
    /// <see cref="Default"/>. A file saved before #173 doesn't say how page life expectancy is
    /// judged: it scales with the buffer pool, unless its threshold had been changed from 300 s,
    /// which was a fixed number of seconds and stays one.
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

        var ple = Info(HealthIndicator.PageLifeExpectancy);
        return result with
        {
            Weights                 = HealthWeights.Normalize(saved.Weights),
            PageLifeExpectancyFixed = saved.PageLifeExpectancyFixed ?? result.Get(ple.Indicator) != ple.Default,
        };
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
