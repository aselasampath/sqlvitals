namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// What a trend page's chart draws (#188): its lines, last week's under them while Compare to
/// baseline is on, and its time axis. The view model decides it; the page draws it in the theme's
/// colours, since those are WPF resources (<c>ChartTheme</c>).
/// </summary>
/// <param name="Baselines">Last week's line under each line, in its colour; empty while the baseline is off.</param>
/// <param name="From">The start of the time axis, on this PC's clock: the range picked; null live, where the axis follows the lines.</param>
/// <param name="To">The end of the time axis; null live.</param>
/// <param name="AxisFormat">How the time axis labels its ticks: "HH:mm:ss" live.</param>
/// <param name="ValueName">The value axis' name: "ms / sec".</param>
public sealed record TrendChart(IReadOnlyList<TrendLine> Lines, IReadOnlyList<TrendLine> Baselines,
                                DateTime? From, DateTime? To, string AxisFormat, string ValueName)
{
    /// <summary>Nothing to draw: no line ticked, or nothing read.</summary>
    public static readonly TrendChart Empty = new([], [], null, null, "HH:mm:ss", "");
}
