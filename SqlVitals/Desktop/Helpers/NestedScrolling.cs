using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// A page's scroller around lists that scroll themselves (#245). The wheel moves the list under the
/// pointer, then the page once the list can go no further: a list keeps the wheel even at its end.
/// A click doesn't move the page under the pointer, or a double-click could land on another row;
/// the keyboard still does, so the row it moves to stays in sight. A combo box keeps the wheel: it
/// changes its value, or scrolls its list.
/// </summary>
internal static class NestedScrolling
{
    private const double LineHeight = 16;   // a ScrollViewer's own line, in pixels

    /// <param name="page">The page's scroller.</param>
    /// <param name="mouseIsDown">Whether a mouse button is down; the mouse itself, but in tests.</param>
    public static void Attach(ScrollViewer page, Func<bool>? mouseIsDown = null)
    {
        mouseIsDown ??= () => Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed;

        page.PreviewMouseWheel += (_, e) =>
        {
            if (page.ScrollableHeight <= 0 || InnerTakesTheWheel(e.OriginalSource as DependencyObject, page, e.Delta))
                return;
            // As far as a list goes for a notch, by the lines per notch set in Windows; -1 is a screenful.
            var lines = SystemParameters.WheelScrollLines;
            var step  = lines > 0 ? lines * LineHeight : page.ViewportHeight;
            page.ScrollToVerticalOffset(page.VerticalOffset - e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * step);
            e.Handled = true;
        };

        // On the content: the scroller's own handler runs before any added to the scroller.
        if (page.Content is UIElement content)
            content.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) =>
            {
                if (mouseIsDown())
                    e.Handled = true;
            }));
    }

    // Any combo box between what's under the pointer and the page, or any scroller that can still go
    // the wheel's way. An open combo box's list is on the way too: its popup leads back to the box.
    private static bool InnerTakesTheWheel(DependencyObject? from, ScrollViewer page, int delta)
    {
        for (var at = from; at is not null && !ReferenceEquals(at, page); at = ParentOf(at))
        {
            if (at is ComboBox)
                return true;
            if (at is ScrollViewer inner && (delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight : inner.VerticalOffset > 0))
                return true;
        }
        return false;
    }

    // A wheel over text starts at a Run, which isn't in the visual tree.
    private static DependencyObject? ParentOf(DependencyObject at) =>
        at is Visual or Visual3D ? VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);
}
