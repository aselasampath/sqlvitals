namespace SqlVitals.Desktop.Services;

/// <summary>A category in Settings' side menu (#131).</summary>
public enum SettingsCategory
{
    Connections,
    Monitoring,
    Thresholds,
    Alerts,
    NotificationArea,
    Appearance,
    Activity,
    Collector,
}

/// <summary>A tab of a connection's page in Settings → Connections (#131).</summary>
public enum ConnectionTab
{
    Connection,
    Monitoring,
    Thresholds,
    Advanced,
}

/// <param name="Subtitle">Under the title. For a category of the whole app it starts "For every connection".</param>
public sealed record SettingsCategoryInfo(SettingsCategory Id, string Title, string Subtitle);

/// <summary>A group of categories in the side menu, under a heading.</summary>
public sealed record SettingsGroup(string Name, IReadOnlyList<SettingsCategoryInfo> Categories);

/// <summary>
/// Where Settings opens: a category, or a connection's tab in Connections, or a new connection.
/// Written "Settings/Connections/&lt;id&gt;/Thresholds", so other pages can link to it.
/// </summary>
public sealed record SettingsLocation(
    SettingsCategory Category,
    Guid?            ConnectionId  = null,
    ConnectionTab    Tab           = ConnectionTab.Connection,
    bool             NewConnection = false)
{
    public static SettingsLocation Connection(Guid id, ConnectionTab tab = ConnectionTab.Connection) =>
        new(SettingsCategory.Connections, id, tab);

    public static SettingsLocation NewConnectionPage { get; } = new(SettingsCategory.Connections, NewConnection: true);

    public override string ToString() =>
        NewConnection ? "Settings/Connections/New"
        : ConnectionId is { } id ? $"Settings/Connections/{id:D}/{Tab}"
        : $"Settings/{Category}";

    /// <summary>Reads what <see cref="ToString"/> wrote; null for anything else.</summary>
    public static SettingsLocation? Parse(string? text)
    {
        var parts = (text ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("Settings", StringComparison.OrdinalIgnoreCase) ||
            !Enum.TryParse<SettingsCategory>(parts[1], ignoreCase: true, out var category) || !Enum.IsDefined(category))
            return null;

        if (parts.Length == 2)
            return new SettingsLocation(category);
        if (category != SettingsCategory.Connections)
            return null;
        if (parts.Length == 3 && parts[2].Equals("New", StringComparison.OrdinalIgnoreCase))
            return NewConnectionPage;
        if (!Guid.TryParse(parts[2], out var id))
            return null;
        if (parts.Length == 3)
            return Connection(id);
        return parts.Length == 4 && Enum.TryParse<ConnectionTab>(parts[3], ignoreCase: true, out var tab) && Enum.IsDefined(tab)
            ? Connection(id, tab)
            : null;
    }
}

/// <summary>Where a setting is changed: a Settings category, a tab of a connection's page, or another page.</summary>
public sealed record SettingPlace(SettingsCategory? Category = null, ConnectionTab? Tab = null, string? Page = null)
{
    public static SettingPlace In(SettingsCategory category) => new(category);
    public static SettingPlace OnTab(ConnectionTab tab) => new(SettingsCategory.Connections, tab);
    public static SettingPlace OnPage(string page) => new(Page: page);
}

/// <summary>
/// Settings' side menu (#131): its groups and categories, in the order shown, and where every
/// setting lives. The settings page builds its menu from this; a setting added to
/// <see cref="SettingsActivity"/> must be placed here, or a test fails.
/// </summary>
public static class SettingsNav
{
    private const string ForEveryConnection = "For every connection. Changes save as you make them.";

    public static readonly IReadOnlyList<SettingsGroup> Groups =
    [
        new("Servers",
        [
            new(SettingsCategory.Connections, "Connections", "Saved encrypted on this PC. Each one appears as a tile on Home."),
        ]),
        new("Monitoring",
        [
            new(SettingsCategory.Monitoring, "Monitoring & history", ForEveryConnection),
            new(SettingsCategory.Thresholds, "Health thresholds",
                "For every connection without its own. They apply when you save."),
            new(SettingsCategory.Collector, "Collector service",
                "For every connection, on this PC: an optional Windows service that keeps watching when SqlVitals is closed."),
        ]),
        new("Notifications",
        [
            new(SettingsCategory.Alerts,           "Alerts & chat",     ForEveryConnection),
            new(SettingsCategory.NotificationArea, "Notification area", ForEveryConnection),
        ]),
        new("App",
        [
            new(SettingsCategory.Appearance, "Appearance",                 "For every connection, on this PC."),
            new(SettingsCategory.Activity,   "Activity log & diagnostics", "For every connection, on this PC."),
        ]),
    ];

    public static IEnumerable<SettingsCategoryInfo> Categories => Groups.SelectMany(g => g.Categories);

    public static SettingsCategoryInfo Info(SettingsCategory category) => Categories.First(c => c.Id == category);

    /// <summary>A tab's title, as its header shows it.</summary>
    public static string Title(ConnectionTab tab) => tab switch
    {
        ConnectionTab.Monitoring => "Monitoring & alerts",
        ConnectionTab.Thresholds => "Health thresholds",
        ConnectionTab.Advanced   => "Advanced",
        _                        => "Connection",
    };

    /// <summary>
    /// The settings for the whole app, by the name the activity log gives them (see
    /// <see cref="SettingsActivity"/>), and where each is changed. "Health threshold: …" covers
    /// every indicator's row and page life expectancy's scaling, "Health weight: …" every weight (#173).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, SettingPlace> AppSettings = new Dictionary<string, SettingPlace>
    {
        ["Start monitoring automatically"]        = SettingPlace.In(SettingsCategory.Monitoring),
        ["Command timeout"]                       = SettingPlace.In(SettingsCategory.Monitoring),
        ["History snapshot interval"]             = SettingPlace.In(SettingsCategory.Monitoring),
        ["History retention"]                     = SettingPlace.In(SettingsCategory.Monitoring),
        [SettingsActivity.QueryTextSetting]       = SettingPlace.In(SettingsCategory.Monitoring),
        ["Health threshold"]                      = SettingPlace.In(SettingsCategory.Thresholds),
        ["Health weight"]                         = SettingPlace.In(SettingsCategory.Thresholds),
        ["Alert samples"]                         = SettingPlace.In(SettingsCategory.Alerts),
        ["Keep running in the notification area"] = SettingPlace.In(SettingsCategory.NotificationArea),
        ["Grid density"]                          = SettingPlace.In(SettingsCategory.Appearance),
        ["When monitoring starts"]                = SettingPlace.In(SettingsCategory.Collector),
        ["Collector history from another PC"]     = SettingPlace.In(SettingsCategory.Collector),
        // Not in the settings file, so logged by Appearance itself.
        ["Theme"]                                 = SettingPlace.In(SettingsCategory.Appearance),
        // On the pages they apply to, not in Settings.
        ["Query regression threshold"]            = SettingPlace.OnPage("Query Regressions"),
        ["Query regression minimum executions"]   = SettingPlace.OnPage("Query Regressions"),
        ["Backup RPO (full)"]                     = SettingPlace.OnPage("Backups"),
        ["Backup RPO (log)"]                      = SettingPlace.OnPage("Backups"),
        ["File latency threshold (data)"]         = SettingPlace.OnPage("File I/O"),
        ["File latency threshold (log)"]          = SettingPlace.OnPage("File I/O"),
        ["File I/O refresh"]                      = SettingPlace.OnPage("File I/O"),
        ["Processes refresh"]                     = SettingPlace.OnPage("Processes"),
        ["Processes auto-refresh"]                = SettingPlace.OnPage("Processes"),
        ["Processes view"]                        = SettingPlace.OnPage("Processes"),
        ["Processes group by login"]              = SettingPlace.OnPage("Processes"),
        ["Query Store charts"]                    = SettingPlace.OnPage("Query Store"),
    };

    /// <summary>A connection's settings, by the name the activity log gives them, and the tab each is on.</summary>
    public static readonly IReadOnlyDictionary<string, ConnectionTab> ConnectionSettings = new Dictionary<string, ConnectionTab>
    {
        ["Name"]                     = ConnectionTab.Connection,
        ["Server"]                   = ConnectionTab.Connection,
        ["Authentication"]           = ConnectionTab.Connection,
        ["User name"]                = ConnectionTab.Connection,
        ["Password"]                 = ConnectionTab.Connection,
        ["Remember password"]        = ConnectionTab.Connection,
        ["Database"]                 = ConnectionTab.Connection,
        ["Encrypt"]                  = ConnectionTab.Connection,
        ["Trust server certificate"] = ConnectionTab.Connection,
        ["Connection timeout"]       = ConnectionTab.Advanced,
        ["Additional parameters"]    = ConnectionTab.Advanced,
        ["Monitor in background"]    = ConnectionTab.Monitoring,
        ["Windows notifications"]    = ConnectionTab.Monitoring,
        ["Own health thresholds"]    = ConnectionTab.Thresholds,
        ["Own health threshold"]     = ConnectionTab.Thresholds,
        ["Own health weight"]        = ConnectionTab.Thresholds,
    };

    /// <summary>Where a setting is changed, by its activity-log name; null for one that isn't placed.</summary>
    public static SettingPlace? PlaceOf(string settingName) =>
        AppSettings.TryGetValue(BaseName(settingName), out var place) ? place : null;

    /// <summary>The tab a connection setting is on, by its activity-log name; null for one that isn't placed.</summary>
    public static ConnectionTab? TabOf(string fieldName) =>
        ConnectionSettings.TryGetValue(BaseName(fieldName), out var tab) ? tab : null;

    // "Health threshold: CPU" → "Health threshold".
    private static string BaseName(string name)
    {
        var colon = name.IndexOf(':');
        return colon < 0 ? name : name[..colon];
    }
}
