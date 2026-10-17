using System.Collections.ObjectModel;
using LiveChartsCore.Defaults;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// One line of a trend (#188): its name in the legend, its points, and which of the theme's series
/// colours it is drawn in. A live line's points are the view model's own collection, so a reading
/// added to it moves the line without the chart being built again.
/// </summary>
/// <param name="Colour">The line's place in the chart's palette (<c>ChartTheme.Series</c>).</param>
/// <param name="Emphasised">Drawn thicker, with larger points: a Query Store query's forced plan (#191).</param>
public sealed record TrendLine(string Name, ObservableCollection<DateTimePoint> Points, int Colour, bool Emphasised = false);
