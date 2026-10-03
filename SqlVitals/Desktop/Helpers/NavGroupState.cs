using System.Windows;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// Marks the sidebar group (#124) that holds the page on screen, so the NavGroupExpander style
/// can point it out while the group is collapsed and its page's button is hidden; and gives the
/// group's header its icon (#141).
/// </summary>
public static class NavGroupState
{
    public static readonly DependencyProperty HoldsCurrentPageProperty =
        DependencyProperty.RegisterAttached("HoldsCurrentPage", typeof(bool), typeof(NavGroupState),
                                            new FrameworkPropertyMetadata(false));

    public static bool GetHoldsCurrentPage(DependencyObject element) => (bool)element.GetValue(HoldsCurrentPageProperty);

    public static void SetHoldsCurrentPage(DependencyObject element, bool value) => element.SetValue(HoldsCurrentPageProperty, value);

    /// <summary>The group's Segoe Fluent Icons glyph, shown before its name.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(NavGroupState), new FrameworkPropertyMetadata(string.Empty));

    public static string GetIcon(DependencyObject element) => (string)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, string value) => element.SetValue(IconProperty, value);
}
