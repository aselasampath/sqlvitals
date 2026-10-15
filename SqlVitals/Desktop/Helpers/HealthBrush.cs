using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// A health level or a tile's status as its colour (<see cref="HealthBrushes"/>), for a view model
/// that says what something means and leaves the brush to the view (#189). "Tint" as the
/// parameter gives a status's pale wash. Null gives the binding's <c>FallbackValue</c>.
/// </summary>
public sealed class HealthBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TileStatus status when parameter is "Tint" => HealthBrushes.Tint(status),
        TileStatus status                          => HealthBrushes.For(status),
        HealthLevel level                          => HealthBrushes.For(level),
        _                                          => DependencyProperty.UnsetValue,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
