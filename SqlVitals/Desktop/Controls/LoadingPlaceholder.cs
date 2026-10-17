using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// What a page's empty grids and its verdict say before it has read anything (#117), so "empty"
/// always means "no rows": the page factory binds <see cref="StateProperty"/> on each page to its
/// view model's <see cref="PageViewModel.LoadState"/>, and every element in the page inherits it.
/// The themes' <c>DataGrid</c> style shows "Loading…" in an empty grid during the first read
/// (<see cref="GridBackgroundKey"/>); <see cref="PageShell"/> says it where the verdict will be.
/// </summary>
public static class LoadingPlaceholder
{
    /// <summary>
    /// The application resource the themes' <c>DataGrid</c> style paints an empty grid with during its
    /// page's first read: "Loading…" where the first row will be, on the grid's own background.
    /// </summary>
    public const string GridBackgroundKey = "GridLoadingBackground";

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(PageLoadState), typeof(LoadingPlaceholder),
            new FrameworkPropertyMetadata(PageLoadState.Ready, FrameworkPropertyMetadataOptions.Inherits, OnStateChanged));

    public static PageLoadState GetState(DependencyObject element) => (PageLoadState)element.GetValue(StateProperty);

    public static void SetState(DependencyObject element, PageLoadState value) => element.SetValue(StateProperty, value);

    /// <summary>What the verdict's place says: "Loading…", why nothing was read, or nothing once the page has read.</summary>
    public static string? VerdictText(PageLoadState state) => state switch
    {
        PageLoadState.Loading => "Loading…",
        PageLoadState.Stopped => "Not read: it was stopped before it finished. Refresh (F5) to read again.",
        _                     => null,
    };

    /// <summary>
    /// Puts the empty grid's "Loading…" (<see cref="GridBackgroundKey"/>) into <paramref name="resources"/>,
    /// in the colours of the theme merged into them now: as the app starts, and after each theme
    /// switch. Its text is in no element tree, so it can't follow the theme itself, and a theme
    /// dictionary can't hold it, since a theme is also read off the UI thread, where no element can be made.
    /// </summary>
    public static void UseTheme(ResourceDictionary resources)
    {
        var text = new TextBlock
        {
            Text = "Loading…", Margin = new Thickness(12, 38, 0, 0), FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            Foreground = (Brush)resources["TextMuted"], HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        // Larger than any grid, so the card's colour fills it wherever the grid is cut off.
        var card = new Border { Width = 4000, Height = 2400, Background = (Brush)resources["BgCard"], Child = text };
        resources[GridBackgroundKey] = new VisualBrush(card) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
    }

    // Inherited: called on each element whose value changes, the page's shell among them.
    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PageShell shell)
            shell.ShowPlaceholder();
    }
}
