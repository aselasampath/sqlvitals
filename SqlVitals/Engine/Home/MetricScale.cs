namespace SqlVitals.Engine.Home;

/// <summary>
/// The vertical scale of the Overview's metric charts (#143): from zero to a round number just
/// above the highest value, so the gridlines read as round figures.
/// </summary>
public static class MetricScale
{
    /// <summary>
    /// The smallest of 1, 2, 2.5 or 5 times a power of ten that is at least <paramref name="highest"/>;
    /// <paramref name="floor"/> when everything is lower, so a flat line at zero still has a scale.
    /// </summary>
    public static double Top(double highest, double floor = 1)
    {
        if (!double.IsFinite(highest) || highest <= floor)
            return floor;

        var power = Math.Pow(10, Math.Floor(Math.Log10(highest)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * power >= highest * (1 - 1e-12))
                return step * power;
        return 10 * power;
    }

    /// <summary>Where a value falls down a chart scaled to <paramref name="top"/>: 0 at the top, 1 at zero.</summary>
    public static double Y(double value, double top) =>
        top <= 0 ? 1 : 1 - Math.Clamp(value / top, 0, 1);
}
