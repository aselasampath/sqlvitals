using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Lays its children side by side, each as wide as its <see cref="SpaceBarPart.Share"/> of the width
/// (#242). A tiny part still shows: it keeps a sliver, taken from the others.
/// </summary>
public sealed class ShareBarPanel : Panel
{
    private const double Sliver = 3;

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = InternalChildren.Cast<FrameworkElement>()
            .Select(c => Math.Max(Sliver, (c.DataContext as SpaceBarPart)?.Share * finalSize.Width ?? 0))
            .ToList();
        var total = widths.Sum();
        var scale = total > finalSize.Width && total > 0 ? finalSize.Width / total : 1;

        double x = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var width = widths[i] * scale;
            InternalChildren[i].Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
