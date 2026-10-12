namespace SqlVitals.Desktop.Services;

/// <summary>The groups the quick switcher's results are in (#144), in the order they show on a tie.</summary>
public enum SwitcherGroup
{
    /// <summary>What was opened lately, before anything is typed.</summary>
    Recent,
    /// <summary>The open server's pages.</summary>
    OnServer,
    /// <summary>A page on another server: the page found, or the page on screen on a server found.</summary>
    OtherServers,
    Servers,
    /// <summary>The pages about every server, or about none: All servers, Alerts, Export, Settings.</summary>
    Pages,
    Settings,
}

/// <summary>A result in the quick switcher (#144).</summary>
/// <param name="Text">What it is called, e.g. "TempDB" or "Processes on PROD-SQL02".</param>
/// <param name="Detail">Where it is, e.g. its group "Resources", or "PROD-SQL01 › Health thresholds".</param>
/// <param name="Route">What opening it opens.</param>
/// <param name="Icon">A <see cref="NavMenu.IconFont"/> glyph; empty where the server's health dot shows.</param>
/// <param name="Server">A server whose health dot and score it shows.</param>
/// <param name="Unavailable">Why it can't be opened (a page Azure SQL Database lacks); null when it can.</param>
/// <param name="MatchStart">Where the typed text is in <paramref name="Text"/>, shown in bold; -1 for none.</param>
public sealed record SwitcherItem(
    SwitcherGroup Group,
    string        Text,
    string        Detail,
    Route         Route,
    string        Icon,
    Guid?         Server      = null,
    string?       Unavailable = null,
    int           MatchStart  = -1,
    int           MatchLength = 0)
{
    public bool CanOpen => Unavailable is null;
}

/// <summary>A group of results, under its heading.</summary>
public sealed record SwitcherSection(SwitcherGroup Group, string Title, IReadOnlyList<SwitcherItem> Items);

/// <summary>A saved connection, as the switcher finds it: by its name or its server's.</summary>
public sealed record SwitcherServer(Guid Id, string Name, string Host);

/// <summary>What the switcher searches, as things are when it opens.</summary>
/// <param name="Current">The route on screen.</param>
/// <param name="Server">The server whose pages are listed as "On …": the open workspace's, else the
/// active connection's, which a server's page opens on from a page about every server.</param>
/// <param name="Servers">The saved connections, in the order the server menu lists them.</param>
/// <param name="Recent">The routes opened lately, most recent first, all still available.</param>
public sealed record SwitcherScope(Route? Current, Guid? Server, IReadOnlyList<SwitcherServer> Servers, IReadOnlyList<Route> Recent)
{
    /// <summary>Why a page can't be opened on a server, or null when it can.</summary>
    public Func<string, Guid, string?> Unavailable { get; init; } = (_, _) => null;
}

/// <summary>
/// The Ctrl+K quick switcher (#144): every page, server and setting, found by name or by the words
/// of its tooltip, without regard to case. Pure: MainWindow passes what is saved and open, shows the
/// results, and opens the one picked through the navigator, so Back returns.
/// </summary>
public static class QuickSwitcher
{
    /// <summary>How many recent routes show before anything is typed.</summary>
    public const int RecentShown = 8;

    // How well a name matches, best first. A tie goes to what was opened most recently.
    internal const int Exact      = 100;
    internal const int Prefix     = 80;
    internal const int WordPrefix = 60;
    internal const int Contains   = 40;
    internal const int Words      = 20;

    // The most each group lists, so a short query doesn't bury the others.
    private const int MaxOnServer = 8;
    private const int MaxOther    = 6;
    private const int MaxServers  = 8;
    private const int MaxSettings = 8;

    /// <summary>
    /// The results for what has been typed, in groups. The group with the best match comes first,
    /// so that its first result, the one selected, is the best; on a tie the groups keep the order of
    /// <see cref="SwitcherGroup"/>. Nothing typed: the recent routes.
    /// </summary>
    public static IReadOnlyList<SwitcherSection> Search(string? query, SwitcherScope scope)
    {
        var q = Normalise(query);
        if (q.Length == 0)
            return Recent(scope);

        string? Name(Guid? id) => scope.Servers.FirstOrDefault(s => s.Id == id)?.Name;
        var serverName = Name(scope.Server);

        var sections = new List<(SwitcherSection Section, int Best)>();
        void Add(SwitcherGroup group, string title, IEnumerable<(SwitcherItem Item, int Score)> found, int max)
        {
            var ranked = Rank(found, scope.Recent).Take(max).ToList();
            if (ranked.Count > 0)
                sections.Add((new SwitcherSection(group, title, ranked.Select(r => r.Item).ToList()), ranked[0].Score));
        }

        var onServer = scope.Server is { } id && serverName is not null ? OnServer(q, scope, id).ToList() : [];
        Add(SwitcherGroup.OnServer, $"On {serverName}", onServer, MaxOnServer);
        Add(SwitcherGroup.OtherServers, "Same page on another server", OtherServers(q, scope, onServer), MaxOther);
        Add(SwitcherGroup.Servers, "Servers", Servers(q, scope), MaxServers);
        Add(SwitcherGroup.Pages, "Pages", Pages(q, scope), int.MaxValue);
        Add(SwitcherGroup.Settings, "Settings", Settings(q, scope, serverName), MaxSettings);

        return sections.OrderByDescending(s => s.Best).ThenBy(s => s.Section.Group).Select(s => s.Section).ToList();
    }

    // ── Matching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// How well <paramref name="query"/> (as <see cref="Normalise"/> leaves it) matches a name, and
    /// where in it: the whole name, its start, the start of one of its words ("stats" in "Stale
    /// Stats"), anywhere in it ("db" in "TempDB"); or, with no score in the name for the query as a
    /// whole, every word of the query starting a word of the name or of <paramref name="words"/>
    /// (a tooltip: "blocking" finds Processes). 0 for no match.
    /// </summary>
    internal static (int Score, int Start, int Length) Match(string query, string name, string? words)
    {
        if (query.Length == 0)
            return (0, -1, 0);
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return (Exact, 0, name.Length);
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return (Prefix, 0, query.Length);
        foreach (var start in WordStarts(name))
            if (start > 0 && string.Compare(name, start, query, 0, query.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return (WordPrefix, start, query.Length);
        var at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
            return (Contains, at, query.Length);

        var text = words is null ? name : $"{name} {words}";
        var starts = WordStarts(text).ToList();
        return query.Split(' ').All(word => starts.Any(s => string.Compare(text, s, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0))
            ? (Words, -1, 0)
            : (0, -1, 0);
    }

    /// <summary>Trimmed, with each run of white space one space.</summary>
    internal static string Normalise(string? query) =>
        string.Join(' ', (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Where each word of a text starts: a letter or digit after anything else.
    private static IEnumerable<int> WordStarts(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (char.IsLetterOrDigit(text[i]) && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
                yield return i;
    }

    // Best match first; on a tie, what was opened most recently, then the order found.
    private static IEnumerable<(SwitcherItem Item, int Score)> Rank(IEnumerable<(SwitcherItem Item, int Score)> found, IReadOnlyList<Route> recent)
    {
        int Recency(Route route)
        {
            for (var i = 0; i < recent.Count; i++)
                if (recent[i].IsSamePlace(route))
                    return i;
            return int.MaxValue;
        }

        return found.Where(f => f.Score > 0)
                    .Select((f, order) => (f.Item, f.Score, Recency: Recency(f.Item.Route), Order: order))
                    .OrderByDescending(f => f.Score).ThenBy(f => f.Recency).ThenBy(f => f.Order)
                    .Select(f => (f.Item, f.Score));
    }

    private static SwitcherItem At(SwitcherItem item, (int Score, int Start, int Length) match, int offset = 0) =>
        match.Start < 0 ? item : item with { MatchStart = match.Start + offset, MatchLength = match.Length };

    // ── The groups ───────────────────────────────────────────────────────────

    private static IReadOnlyList<SwitcherSection> Recent(SwitcherScope scope)
    {
        string? Name(Guid id) => scope.Servers.FirstOrDefault(s => s.Id == id)?.Name;

        // The page on screen is where the switcher was opened from: not somewhere to go.
        var items = scope.Recent
            .Where(r => scope.Current is null || !r.IsSamePlace(scope.Current))
            .Take(RecentShown)
            .Select(route =>
            {
                var server = Workspace.ServerOf(route);
                var group  = NavMenu.GroupOf(route.Tag)?.Name ?? string.Empty;
                return new SwitcherItem(SwitcherGroup.Recent, Breadcrumb.Describe(route, Name), group == Label(route.Tag) ? string.Empty : group,
                                        route, Icon(route.Tag), server,
                                        route.ConnectionId is { } id ? scope.Unavailable(route.Tag, id) : null);
            })
            .ToList();
        return items.Count == 0 ? [] : [new SwitcherSection(SwitcherGroup.Recent, "Recent", items)];
    }

    // The server's pages, by name or by the words of their tooltip and group.
    private static IEnumerable<(SwitcherItem, int)> OnServer(string q, SwitcherScope scope, Guid server)
    {
        foreach (var group in NavMenu.Groups)
            foreach (var page in group.Pages)
            {
                var match = Match(q, page.Label, $"{page.ToolTip} {group.Name}");
                if (match.Score == 0)
                    continue;
                var item = new SwitcherItem(SwitcherGroup.OnServer, page.Label, group.Name, PageOn(page.Tag, server), page.Icon,
                                            Unavailable: scope.Unavailable(page.Tag, server));
                yield return (At(item, match), match.Score);
            }
    }

    // A server's page: its Overview by server, any other by the connection it queries.
    private static Route PageOn(string tag, Guid server) =>
        tag == "Overview" ? Workspace.Overview(server) : new Route(tag, ConnectionId: server);

    // The best page found, on each other server; and, for a server found by name, the page on screen
    // there: type a server's name to switch server and keep the page. Only inside a server's workspace.
    private static IEnumerable<(SwitcherItem, int)> OtherServers(string q, SwitcherScope scope, IReadOnlyList<(SwitcherItem Item, int Score)> onServer)
    {
        if (scope.Current is not { } current || Workspace.ServerOf(current) is not { } here)
            yield break;

        var others = scope.Servers.Where(s => s.Id != here).ToList();
        var seen   = new HashSet<Route>();

        if (Rank(onServer, scope.Recent).FirstOrDefault() is { Item: { } best, Score: var bestScore })
            foreach (var other in others)
            {
                var route = PageOn(best.Route.Tag, other.Id);
                if (seen.Add(route))
                    yield return (new SwitcherItem(SwitcherGroup.OtherServers, $"{best.Text} on {other.Name}", other.Host, route, string.Empty, other.Id,
                                                   scope.Unavailable(route.Tag, other.Id), best.MatchStart, best.MatchLength), bestScore);
            }

        var page = Label(current.Tag);
        foreach (var other in others)
        {
            var match = Match(q, other.Name, other.Host);
            var route = Workspace.OnServer(current, other.Id);
            if (match.Score == 0 || !seen.Add(route))
                continue;
            var item = new SwitcherItem(SwitcherGroup.OtherServers, $"{page} on {other.Name}", other.Host, route, string.Empty, other.Id,
                                        route.ConnectionId is { } id ? scope.Unavailable(route.Tag, id) : null);
            yield return (At(item, match, page.Length + " on ".Length), match.Score);
        }
    }

    // A server by its name, or its server's: its Overview.
    private static IEnumerable<(SwitcherItem, int)> Servers(string q, SwitcherScope scope)
    {
        foreach (var server in scope.Servers)
        {
            var match = Match(q, server.Name, server.Host);
            if (match.Score > 0)
                yield return (At(new SwitcherItem(SwitcherGroup.Servers, server.Name, server.Host, Workspace.Overview(server.Id), string.Empty, server.Id), match),
                              match.Score);
        }
    }

    // All servers, Alerts, Export / AI and Settings. Export reads the open server, as from the sidebar.
    private static IEnumerable<(SwitcherItem, int)> Pages(string q, SwitcherScope scope)
    {
        foreach (var page in NavMenu.Global.Concat(NavMenu.Footer))
        {
            var match = Match(q, page.Label, page.ToolTip);
            if (match.Score == 0)
                continue;
            var route = Route.IsGlobal(page.Tag) ? new Route(page.Tag) : new Route(page.Tag, ConnectionId: scope.Server);
            yield return (At(new SwitcherItem(SwitcherGroup.Pages, page.Label, string.Empty, route, page.Icon), match), match.Score);
        }
    }

    // Settings' categories, the open server's connection tabs, and each setting by the name the
    // activity log gives it, opening where it is changed.
    private static IEnumerable<(SwitcherItem, int)> Settings(string q, SwitcherScope scope, string? serverName)
    {
        var icon = Icon("Settings");
        (SwitcherItem, int)? Found(string text, string detail, Route route, string? words)
        {
            var match = Match(q, text, words);
            return match.Score == 0 ? null : (At(new SwitcherItem(SwitcherGroup.Settings, text, detail, route, icon), match), match.Score);
        }
        static Route In(SettingsLocation location) => new("Settings", Settings: location);

        var found = new List<(SwitcherItem, int)?>();
        foreach (var group in SettingsNav.Groups)
            foreach (var category in group.Categories)
                found.Add(Found(category.Title, "Settings", In(new SettingsLocation(category.Id)), $"{category.Subtitle} {group.Name}"));

        if (scope.Server is { } server && serverName is not null)
            foreach (var tab in Enum.GetValues<ConnectionTab>())
                found.Add(Found(SettingsNav.Title(tab), $"{serverName}'s connection", In(SettingsLocation.Connection(server, tab)), "connection settings"));

        foreach (var (name, place) in SettingsNav.AppSettings)
        {
            if (place.Category is { } category)
                found.Add(Found(name, $"Settings › {SettingsNav.Info(category).Title}", In(new SettingsLocation(category)), null));
            else if (scope.Server is { } id && NavMenu.AllPages.FirstOrDefault(p => p.Label == place.Page) is { } page)
                found.Add(Found(name, $"On {page.Label}", PageOn(page.Tag, id), null));
        }

        if (scope.Server is { } connection && serverName is not null)
            foreach (var (name, tab) in SettingsNav.ConnectionSettings)
                found.Add(Found(name, $"{serverName} › {SettingsNav.Title(tab)}", In(SettingsLocation.Connection(connection, tab)), null));

        return found.OfType<(SwitcherItem, int)>();
    }

    private static string Label(string tag) => NavMenu.AllPages.FirstOrDefault(p => p.Tag == tag)?.Label ?? tag;

    private static string Icon(string tag) => NavMenu.AllPages.FirstOrDefault(p => p.Tag == tag)?.Icon ?? string.Empty;
}

/// <summary>
/// The routes opened this session, most recent first (#144): the quick switcher lists them before
/// anything is typed. The same place opened again moves to the front rather than showing twice.
/// </summary>
public sealed class RecentRoutes
{
    public const int Capacity = 20;

    private readonly List<Route> _routes = [];

    public IReadOnlyList<Route> Items => _routes;

    public void Add(Route route)
    {
        // The page, not the alert it was opened for (#145): its banner would be out of date. Nor the
        // part of it a link opened on (#178): the page opens on the part last chosen.
        route = route with { Focus = null, View = null };
        _routes.RemoveAll(r => r.IsSamePlace(route));
        _routes.Insert(0, route);
        if (_routes.Count > Capacity)
            _routes.RemoveAt(_routes.Count - 1);
    }
}
