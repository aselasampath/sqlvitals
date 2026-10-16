using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A Settings status line (#190): shows its <see cref="Line"/>, a view model's
/// <see cref="StatusLine"/>, through the <c>Status</c> style, which binds to <c>Line</c> on the
/// element itself. Not the line as the element's <c>DataContext</c>: the style would bind to the
/// category's view model first, and WPF would report every path as missing.
/// </summary>
public sealed class StatusText : TextBlock
{
    public static readonly DependencyProperty LineProperty =
        DependencyProperty.Register(nameof(Line), typeof(StatusLine), typeof(StatusText));

    public StatusLine? Line
    {
        get => (StatusLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }
}
