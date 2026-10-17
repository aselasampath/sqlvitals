using SqlVitals.Desktop.Pages;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// The window's side of showing a page (#191): the page itself, which the shell's view model never
/// holds. The shell decides which route to show and when; the host puts the page on screen, with
/// its own controls in the top bar and its details in the drawer, and keeps how each route's page
/// was being looked at (its sort, filter and scroll position, <see cref="Helpers.ViewState"/>).
/// </summary>
public interface IPageHost
{
    /// <summary>
    /// Shows a page the factory made in place of the one on screen: its controls go to the top bar
    /// (the previous page's go), it is filtered to <paramref name="focus"/> when opened as evidence
    /// (#145), and the details drawer follows it.
    /// </summary>
    /// <returns>What the page tells the shell about itself.</returns>
    ShownPage Show(PageInstance page, PageFocus? focus);

    /// <summary>Keeps how the page on screen is being looked at, for when <paramref name="route"/> is opened again.</summary>
    void KeepViewState(Route route);

    /// <summary>
    /// Puts back how <paramref name="route"/>'s page was being looked at, if it was: its tab and
    /// grids before it loads, then, once loaded, its scroll position.
    /// </summary>
    void RestoreViewState(Route route, bool scrolling);
}

/// <summary>What the page on screen tells the shell (#191): its kind and freshness for the top bar, and its filter as evidence.</summary>
/// <param name="Context">What the top bar says about it (<see cref="IPageContext"/>); every page has one.</param>
/// <param name="Focus">Its filter, for a page that can be opened as evidence; null otherwise.</param>
public sealed record ShownPage(IPageContext? Context, IFocusPage? Focus);
