using System.Collections.ObjectModel;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace SqlVitals.Desktop.Helpers;

// How a baseline (the same time last week) is drawn under a trend: its series' colour, faded
// and dashed, with no point markers, and named "… · last week" in the legend and tooltip. It
// reads as reference rather than data, and stays tied to the line it is compared with.
public static class BaselineSeries
{
    public const string Suffix = " · last week";

    public static LineSeries<DateTimePoint> Line(
        ObservableCollection<DateTimePoint> values, string name, SKColor color, int scalesAt = 0) =>
        new()
        {
            Values         = values,
            Name           = name + Suffix,
            Stroke         = new SolidColorPaint(color.WithAlpha(170))
            {
                StrokeThickness = 1.6f,
                PathEffect      = new DashEffect([6f, 4f]),
            },
            Fill           = null,
            GeometrySize   = 0,
            GeometryFill   = null,
            GeometryStroke = null,
            LineSmoothness = 0,
            ScalesYAt      = scalesAt,
        };
}
