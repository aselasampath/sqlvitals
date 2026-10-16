using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// Visible or collapsed from a view model's value (#190): true, a text that isn't empty, a list that
/// isn't, anything else that isn't null. "Not" as the parameter turns it round.
/// </summary>
public sealed class Shown : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var shown = value switch
        {
            bool b          => b,
            string s        => s.Length > 0,
            ICollection c   => c.Count > 0,
            null            => false,
            _               => true,
        };
        if (parameter is "Not")
            shown = !shown;
        return shown ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
