using System.Globalization;
using System.Windows.Data;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// A share of a length (#245): the parameter is the fraction, such as 0.33, so a list can be limited
/// to part of the height it shares with another and still scroll.
/// </summary>
public sealed class FractionOf : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (double)value! * double.Parse((string)parameter!, CultureInfo.InvariantCulture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
