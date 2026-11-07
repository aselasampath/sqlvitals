using System.Windows;
using System.Windows.Controls;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Holds a part of a page with nothing to show yet: hidden, and taking no room, while its content is
/// still measured and arranged at full size. A DataGrid first measured collapsed, or squeezed to no
/// height, keeps its columns at their least width when it appears (#243); this one never is.
/// </summary>
public sealed class KeptInLayout : Decorator
{
    public static readonly DependencyProperty ShowsProperty =
        DependencyProperty.Register(nameof(Shows), typeof(bool), typeof(KeptInLayout),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((KeptInLayout)d).Follow((bool)e.NewValue)));

    public KeptInLayout() => Follow(Shows);

    /// <summary>Shown, and taking the room it needs.</summary>
    public bool Shows
    {
        get => (bool)GetValue(ShowsProperty);
        set => SetValue(ShowsProperty, value);
    }

    private void Follow(bool shows) => Visibility = shows ? Visibility.Visible : Visibility.Hidden;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
            return default;
        child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(child.DesiredSize.Width, Shows ? child.DesiredSize.Height : 0);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }
}
