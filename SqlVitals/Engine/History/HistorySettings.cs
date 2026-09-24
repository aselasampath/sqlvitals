using System.Globalization;

namespace SqlVitals.Engine.History;

/// <summary>
/// The choices Settings offers for the monitoring history: how often a detail snapshot is taken
/// (see <see cref="HistoryRecorder.DetailInterval"/>) and how long history is kept (see
/// <see cref="HistoryWriter.Retention"/>). Live Metrics samples are saved at their own rate.
/// </summary>
public static class HistorySettings
{
    public const int DefaultIntervalMinutes = 5;
    public const int DefaultRetentionDays   = 14;

    public static IReadOnlyList<int> IntervalChoicesMinutes { get; } = [1, 5, 15, 30];
    public static IReadOnlyList<int> RetentionChoicesDays   { get; } = [7, 14, 30, 90];

    /// <summary>
    /// The offered interval closest to a saved one (a hand-edited file, say); the default when
    /// none was saved.
    /// </summary>
    public static int NormalizeInterval(int minutes) => Closest(IntervalChoicesMinutes, minutes, DefaultIntervalMinutes);

    /// <summary>The offered retention closest to a saved one; the default when none was saved.</summary>
    public static int NormalizeRetention(int days) => Closest(RetentionChoicesDays, days, DefaultRetentionDays);

    // On a tie the smaller choice wins: less load on the server, less disk.
    private static int Closest(IReadOnlyList<int> choices, int value, int fallback) =>
        value <= 0 ? fallback : choices.MinBy(c => Math.Abs((long)c - value));

    /// <summary>"820 KB", "12.4 MB", "1.25 GB": a file size for the Settings page.</summary>
    public static string FormatSize(long bytes)
    {
        var culture = CultureInfo.CurrentCulture;
        return bytes switch
        {
            < 1024                => string.Format(culture, "{0} bytes", bytes),
            < 1024 * 1024         => string.Format(culture, "{0:0} KB", bytes / 1024.0),
            < 1024L * 1024 * 1024 => string.Format(culture, "{0:0.0} MB", bytes / (1024.0 * 1024)),
            _                     => string.Format(culture, "{0:0.00} GB", bytes / (1024.0 * 1024 * 1024)),
        };
    }
}
