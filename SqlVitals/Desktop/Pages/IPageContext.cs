using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// What a page tells the top bar about itself (#140): whether it is live, a snapshot or a time
/// window, and the controls for that. Every page implements it; <c>PageCatalogTests</c> fails for
/// one that doesn't. A page calls <see cref="Services.IShellStatus.ReportContextChanged"/> when any of it changes.
/// </summary>
public interface IPageContext
{
    /// <summary>What the page shows now. A page with a time range picker is History while a past range is picked.</summary>
    PageKind Kind { get; }

    /// <summary>How often a live page updates; null when the bar shouldn't say (its own control does, or it varies).</summary>
    TimeSpan? LiveInterval => null;

    /// <summary>A live page whose updates can be paused from the bar.</summary>
    bool CanPause => false;

    bool IsPaused => false;

    /// <summary>Pause or resume a live page's updates, as its own Start/Stop would.</summary>
    void SetPaused(bool paused) { }

    /// <summary>A history page's window, for one whose picker stays on the page.</summary>
    string? HistoryWindow => null;

    /// <summary>What a history page compares its window with, where it does and its picker stays on the page.</summary>
    string? CompareWith => null;

    /// <summary>The newest data a history page shows, on this PC's clock.</summary>
    DateTime? DataTo => null;

    /// <summary>
    /// Pages that read the connection's database rather than the whole server: Query Store, index
    /// health and the like. The bar shows that database.
    /// </summary>
    bool UsesDatabase => false;

    /// <summary>
    /// Controls the page hands to the bar, taken out of its own layout: its time range picker,
    /// Live Metrics' sample interval. Called once, before the page loads; they keep their handlers.
    /// </summary>
    FrameworkElement? TakeBarTools() => null;
}

/// <summary>Helpers for pages implementing <see cref="IPageContext"/>.</summary>
internal static class PageBar
{
    /// <summary>Takes an element out of the page's layout, for the bar.</summary>
    public static FrameworkElement Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel:              panel.Children.Remove(element); break;
            case Decorator decorator:      decorator.Child = null; break;
            case ContentControl control:   control.Content = null; break;
        }
        return element;
    }

    /// <summary>
    /// The end of the newest data in a history read, on this PC's clock: the last bucket's end, but
    /// never past the window. Null when the window had nothing.
    /// </summary>
    public static DateTime? DataTo<T>(HistoryLoad<T>? load, Func<T, DateTime> timeUtc) =>
        load is { Items.Count: > 0 }
            ? new DateTime(Math.Min((timeUtc(load.Items[^1]) + load.Bucket).Ticks, load.ToUtc.Ticks), DateTimeKind.Utc).ToLocalTime()
            : null;
}
