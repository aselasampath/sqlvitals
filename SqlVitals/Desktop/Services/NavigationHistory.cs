namespace SqlVitals.Desktop.Services;

/// <summary>
/// A place in the app (#139): a page, the connection it shows, and what it was opened with.
/// Back and Forward return to routes, and a page's view state is kept per route.
/// </summary>
/// <param name="Tag">The page's tag in <see cref="NavMenu"/>.</param>
/// <param name="ConnectionId">The connection a server's page queries; null for a page about every
/// connection (Home, Alerts, Settings) or about one by <paramref name="ServerId"/>.</param>
/// <param name="ServerId">The connection an Overview (#143) shows.</param>
/// <param name="AlertsConnectionId">The connection the Alerts page is filtered to, from a notification.</param>
/// <param name="Settings">Where Settings opens (#131); null for the category last used.</param>
/// <param name="Focus">What the page was opened to show (#145): an alert's evidence, filtered and
/// set to its time, or a link's session or query. Null for the page as the sidebar opens it.</param>
/// <param name="View">The part of the page it opens on, for a page that took in another (#178):
/// Processes' Waiting view, Plan Cache Health's Implicit Conversions tab. Null for the page's own choice.</param>
public sealed record Route(string Tag, Guid? ConnectionId = null, Guid? ServerId = null,
                           Guid? AlertsConnectionId = null, SettingsLocation? Settings = null,
                           PageFocus? Focus = null, string? View = null)
{
    /// <summary>
    /// Pages that read every connection, or a connection named in the route, rather than the
    /// active one: they need no active connection and never switch it.
    /// </summary>
    public static bool IsGlobal(string tag) => tag is "Home" or "Overview" or "Alerts" or "Settings";

    /// <summary>
    /// The same page about the same server, opened with different details: Settings on another
    /// category, Alerts filtered from a notification, or a page opened as evidence (#145). Moving
    /// between them replaces the current entry instead of adding one, so Back doesn't stop at the
    /// same page twice. Another part of a page (<see cref="View"/>, #178) is another place: a
    /// session's waits opened from the map, and Back returns to the map.
    /// </summary>
    public bool IsSamePlace(Route other) =>
        Tag == other.Tag && ConnectionId == other.ConnectionId && ServerId == other.ServerId && View == other.View;
}

/// <summary>
/// The routes visited this session (#139), for Back and Forward. Pure: MainWindow decides which
/// routes are still available (a connection may have been removed since) and does the navigating;
/// a move is only recorded once the navigation has happened, since leaving Settings can be refused.
/// </summary>
public sealed class NavigationHistory
{
    /// <summary>How many routes Back and Forward can reach, together.</summary>
    public const int Capacity = 50;

    // Most recent last.
    private readonly List<Route> _back    = [];
    private readonly List<Route> _forward = [];

    /// <summary>The page on screen; null before the first navigation.</summary>
    public Route? Current { get; private set; }

    /// <summary>
    /// Records a navigation by link, sidebar or selector: the current route goes onto the Back
    /// list and the Forward list is dropped, as in a browser. The same place again replaces the
    /// current entry.
    /// </summary>
    public void Visit(Route route)
    {
        if (Current is not null && !Current.IsSamePlace(route))
        {
            _back.Add(Current);
            _forward.Clear();
            Trim();
        }
        Current = route;
    }

    /// <summary>
    /// Changes the route of the page on screen without a move: its focus cleared by Show all
    /// (#145), so Back and Forward return to the page as it is now.
    /// </summary>
    public void ReplaceCurrent(Route route)
    {
        if (Current is not null)
            Current = route;
    }

    /// <summary>
    /// The route Back would open: the most recent one still available. Null when there is none.
    /// Nothing changes until <see cref="CommitBack"/>.
    /// </summary>
    public Route? PeekBack(Func<Route, bool> isAvailable) => Peek(_back, isAvailable);

    /// <inheritdoc cref="PeekBack"/>
    public Route? PeekForward(Func<Route, bool> isAvailable) => Peek(_forward, isAvailable);

    /// <summary>
    /// Records that Back opened <paramref name="target"/> (from <see cref="PeekBack"/>), showing
    /// <paramref name="shown"/>: the same route, or where it led instead (e.g. Settings, to ask
    /// for a password). Routes skipped on the way were unavailable and are dropped.
    /// </summary>
    public void CommitBack(Route target, Route shown) => Commit(_back, _forward, target, shown);

    /// <inheritdoc cref="CommitBack"/>
    public void CommitForward(Route target, Route shown) => Commit(_forward, _back, target, shown);

    private static Route? Peek(List<Route> stack, Func<Route, bool> isAvailable)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
            if (isAvailable(stack[i]))
                return stack[i];
        return null;
    }

    private void Commit(List<Route> from, List<Route> to, Route target, Route shown)
    {
        var index = from.LastIndexOf(target);
        if (index < 0)
        {
            // The lists changed since the peek; treat it as an ordinary visit.
            Visit(shown);
            return;
        }

        from.RemoveRange(index, from.Count - index);
        if (Current is not null)
            to.Add(Current);
        Current = shown;
        Trim();
    }

    // The oldest routes go first, from whichever end is furthest from the current page.
    private void Trim()
    {
        while (_back.Count + _forward.Count > Capacity)
        {
            if (_back.Count >= _forward.Count)
                _back.RemoveAt(0);
            else
                _forward.RemoveAt(0);
        }
    }
}

/// <summary>
/// The last <see cref="Capacity"/> routes' saved values, most recently saved kept longest: a
/// page's view state, so it opens as it was left (#139).
/// </summary>
public sealed class RouteStates<T> where T : class
{
    public const int Capacity = 50;

    // Most recently saved last.
    private readonly List<(Route Route, T Value)> _items = [];

    public void Save(Route route, T value)
    {
        Remove(route);
        _items.Add((route, value));
        if (_items.Count > Capacity)
            _items.RemoveAt(0);
    }

    public T? Get(Route route) => _items.FirstOrDefault(i => i.Route == route).Value;

    public void Remove(Route route) => _items.RemoveAll(i => i.Route == route);

    public int Count => _items.Count;
}
