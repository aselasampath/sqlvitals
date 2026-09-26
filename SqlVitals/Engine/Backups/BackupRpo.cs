using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlVitals.Engine.Backups;

/// <summary>
/// The recovery point objectives the Backups page checks each database against (#40): how old
/// its last full backup may get, and, in the FULL or BULK_LOGGED recovery model, its last log
/// backup. Saved in Settings and changed on the page.
/// </summary>
public sealed record BackupRpo(TimeSpan FullMaxAge, TimeSpan LogMaxAge)
{
    /// <summary>A weekly full backup, with differentials in between, is common, so a week.</summary>
    public static readonly TimeSpan DefaultFullMaxAge = TimeSpan.FromDays(7);

    /// <summary>An RPO of an hour: log backups every 15 minutes leave room for a few to be late.</summary>
    public static readonly TimeSpan DefaultLogMaxAge = TimeSpan.FromHours(1);

    public static readonly TimeSpan MinFullMaxAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxFullMaxAge = TimeSpan.FromDays(400);
    public static readonly TimeSpan MinLogMaxAge  = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLogMaxAge  = TimeSpan.FromDays(30);

    public static BackupRpo Default { get; } = new(DefaultFullMaxAge, DefaultLogMaxAge);

    /// <summary>"full backups older than 7 d, log backups older than 1 h".</summary>
    public string Describe() => $"full backups older than {Format(FullMaxAge)}, log backups older than {Format(LogMaxAge)}";

    /// <summary>A saved full-backup threshold in minutes, or the default when missing or out of range.</summary>
    public static TimeSpan NormalizeFull(int minutes) =>
        Normalize(minutes, MinFullMaxAge, MaxFullMaxAge, DefaultFullMaxAge);

    /// <summary>A saved log-backup threshold in minutes, or the default when missing or out of range.</summary>
    public static TimeSpan NormalizeLog(int minutes) =>
        Normalize(minutes, MinLogMaxAge, MaxLogMaxAge, DefaultLogMaxAge);

    private static TimeSpan Normalize(int minutes, TimeSpan min, TimeSpan max, TimeSpan fallback)
    {
        var value = TimeSpan.FromMinutes(minutes);
        return value >= min && value <= max ? value : fallback;
    }

    /// <summary>
    /// Reads the two boxes on the page. Each takes a number with a unit, "7d", "26 h", "90 min",
    /// "1d 12h", or a plain number in the box's own unit: days for the full backup, minutes for
    /// the log. False, with a message to show, when either can't be used.
    /// </summary>
    public static bool TryParse(string fullText, string logText, out BackupRpo? rpo, out string error)
    {
        rpo = null;

        if (TryParseAge(fullText, TimeSpan.FromDays(1)) is not { } full || full < MinFullMaxAge || full > MaxFullMaxAge)
        {
            error = $"Enter the full backup age as a number of days, or with a unit (26h, 1d 12h), from {Format(MinFullMaxAge)} to {Format(MaxFullMaxAge)}.";
            return false;
        }

        if (TryParseAge(logText, TimeSpan.FromMinutes(1)) is not { } log || log < MinLogMaxAge || log > MaxLogMaxAge)
        {
            error = $"Enter the log backup age as a number of minutes, or with a unit (2h, 90 min), from {Format(MinLogMaxAge)} to {Format(MaxLogMaxAge)}.";
            return false;
        }

        rpo   = new BackupRpo(full, log);
        error = string.Empty;
        return true;
    }

    private static readonly Regex AgePart = new(
        @"\G\s*(?<n>\d{1,6})\s*(?<unit>days?|d|hours?|hrs?|h|minutes?|mins?|m)?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// "7", "7d", "26 h", "1d 12h", "90 min": whole numbers, each with an optional unit. A number
    /// without one is in <paramref name="defaultUnit"/>, and only allowed on its own.
    /// </summary>
    internal static TimeSpan? TryParseAge(string? text, TimeSpan defaultUnit)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return null;

        var total = TimeSpan.Zero;
        var index = 0;
        var parts = 0;
        var bare  = false;
        while (index < text.Length)
        {
            var match = AgePart.Match(text, index);
            if (!match.Success || match.Length == 0)
                return null;

            var n    = long.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            total += unit switch
            {
                ""                   => defaultUnit * n,
                var u when u[0] == 'd' => TimeSpan.FromDays(n),
                var u when u[0] == 'h' => TimeSpan.FromHours(n),
                _                    => TimeSpan.FromMinutes(n),
            };
            bare  |= unit.Length == 0;
            index += match.Length;
            parts++;
        }

        // "1 12" is ambiguous; a bare number is only the whole value.
        return bare && parts > 1 ? null : total;
    }

    /// <summary>
    /// "7 d", "26 h", "3 d 12 h", "90 min": a threshold as the boxes show it, which
    /// <see cref="TryParse"/> reads back to the same value.
    /// </summary>
    public static string Format(TimeSpan age)
    {
        var minutes = Math.Max(0, (long)Math.Round(age.TotalMinutes));
        long days = minutes / 1440, hours = minutes / 60 % 24, mins = minutes % 60;

        if (minutes < 180 && mins != 0) return $"{minutes} min";
        if (mins != 0)                  return Join(days, hours, mins);
        if (hours == 0 && days > 0)     return $"{days} d";
        if (minutes < 72 * 60)          return $"{minutes / 60} h";
        return Join(days, hours, 0);

        static string Join(long d, long h, long m) => string.Join(" ",
            new[] { d > 0 ? $"{d} d" : null, h > 0 ? $"{h} h" : null, m > 0 ? $"{m} min" : null }.OfType<string>());
    }
}
