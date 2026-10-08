using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// A page that can be opened as evidence (#145): shown filtered to the problem, or set to an
/// alert's time window, until Show all. MainWindow calls <see cref="ShowFocus"/> once, before the
/// page loads; the filter stays through its refreshes. The page calls
/// <see cref="Services.IShellStatus.ReportContextChanged"/> when <see cref="FocusNote"/> changes.
/// </summary>
public interface IFocusPage
{
    /// <summary>Shows only what the focus names, from the first load on.</summary>
    void ShowFocus(PageFocus focus);

    /// <summary>Shows everything again, as the sidebar opens the page: Show all.</summary>
    void ClearFocus();

    /// <summary>
    /// What the page shows of its rows now, for the banner: "showing the blocking chain of session
    /// 87 only". Null when it has nothing to add, e.g. before its first load.
    /// </summary>
    string? FocusNote { get; }

    /// <summary>Show all's words: "Show all 214 sessions". Null when the focus filters nothing on this page.</summary>
    string? ShowAllText { get; }

    /// <summary>The page shows the focus's time window itself: it reads the monitoring history.</summary>
    bool ShowsWindow => false;
}
