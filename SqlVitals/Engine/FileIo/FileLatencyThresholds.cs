using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlVitals.Engine.FileIo;

/// <summary>
/// The average read or write latency above which the File I/O page highlights a file (#41): one
/// for data files and one for log files, whose writes every commit waits on. A file above
/// <see cref="CriticalFactor"/> times its threshold is shown in red. Saved in Settings and
/// changed on the page.
/// </summary>
public sealed record FileLatencyThresholds(double DataMs, double LogMs)
{
    /// <summary>
    /// Data file reads and writes over 20 ms are the usual sign of storage that can't keep up
    /// (and what Microsoft's guidance calls a problem); well under 10 ms is normal on SSDs.
    /// </summary>
    public const double DefaultDataMs = 20;

    /// <summary>A commit waits for its log write, so log files are held to a lower bar.</summary>
    public const double DefaultLogMs = 10;

    public const double MinMs = 0.5;
    public const double MaxMs = 10_000;

    /// <summary>Red from this many times the threshold: 100 ms for data files by default, where reads are bad by any measure.</summary>
    public const double CriticalFactor = 5;

    public static FileLatencyThresholds Default { get; } = new(DefaultDataMs, DefaultLogMs);

    /// <summary>The threshold for a file of this type: LOG files have their own, the rest are data.</summary>
    public double For(bool isLog) => isLog ? LogMs : DataMs;

    /// <summary>"data files over 20 ms, log files over 10 ms".</summary>
    public string Describe() => $"data files over {Format(DataMs)}, log files over {Format(LogMs)}";

    /// <summary>A saved data-file threshold, or the default when missing or out of range.</summary>
    public static double NormalizeData(double ms) => Normalize(ms, DefaultDataMs);

    /// <summary>A saved log-file threshold, or the default when missing or out of range.</summary>
    public static double NormalizeLog(double ms) => Normalize(ms, DefaultLogMs);

    private static double Normalize(double ms, double fallback) =>
        double.IsFinite(ms) && ms >= MinMs && ms <= MaxMs ? ms : fallback;

    private static readonly Regex Milliseconds = new(
        @"^\s*(?<n>\d{1,5}(?:[.,]\d{1,2})?)\s*(?:ms|msec|milliseconds?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the two boxes on the page: milliseconds, "20", "20 ms", "2.5". False, with a
    /// message to show, when either can't be used.
    /// </summary>
    public static bool TryParse(string dataText, string logText, out FileLatencyThresholds? thresholds, out string error)
    {
        thresholds = null;

        if (TryParseMs(dataText) is not { } data)
        {
            error = $"Enter the data file threshold in milliseconds (20, or 20 ms), from {Format(MinMs)} to {Format(MaxMs)}.";
            return false;
        }

        if (TryParseMs(logText) is not { } log)
        {
            error = $"Enter the log file threshold in milliseconds (10, or 2.5 ms), from {Format(MinMs)} to {Format(MaxMs)}.";
            return false;
        }

        thresholds = new FileLatencyThresholds(data, log);
        error      = string.Empty;
        return true;
    }

    internal static double? TryParseMs(string? text)
    {
        var match = Milliseconds.Match(text ?? string.Empty);
        if (!match.Success)
            return null;

        // "2,5" is 2.5 wherever a comma is the decimal separator; either is accepted everywhere.
        var ms = double.Parse(match.Groups["n"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return ms is >= MinMs and <= MaxMs ? ms : null;
    }

    /// <summary>"20 ms", "2.5 ms": a threshold as the boxes show it, which <see cref="TryParse"/> reads back.</summary>
    public static string Format(double ms) => ms.ToString("0.##", CultureInfo.InvariantCulture) + " ms";
}
