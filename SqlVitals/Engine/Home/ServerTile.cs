using System.Globalization;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Home;

/// <summary>What a Home tile says about a connection (#130), worst first.</summary>
public enum TileStatus
{
    /// <summary>Monitored, but the last collection failed: the score is 0.</summary>
    Unreachable,
    Critical,
    Warning,
    Healthy,
    /// <summary>Monitored, before its first collection has finished.</summary>
    Waiting,
    /// <summary>Not monitored, so there is no score.</summary>
    Off,
}

/// <summary>What a tile's button does.</summary>
public enum TileAction { Open, Retry, Start }

/// <summary>Everything Home knows about one saved connection; never a credential.</summary>
/// <param name="Database">The database label, e.g. "Sales" or "(default database)".</param>
/// <param name="Score">The latest score; null while not monitored or before the first sample.</param>
/// <param name="LastSuccess">When the last collection that worked finished, on this PC's clock.</param>
/// <param name="FailingSince">When collections started failing, on this PC's clock; null while they work.</param>
/// <param name="LastError">Why the last collection failed, credentials masked.</param>
/// <param name="NotMonitoredReason">Why it isn't monitored, e.g. "Stopped by you at 10:42…".</param>
/// <param name="CanStart">Start can be pressed: false while the password or the sign-in is missing.</param>
public sealed record TileInput(
    Guid         Id,
    string       Name,
    string       Server,
    string       Database,
    bool         IsActive,
    bool         IsMonitored,
    HealthScore? Score,
    DateTime?    LastSuccess        = null,
    DateTime?    FailingSince       = null,
    string?      LastError          = null,
    string?      NotMonitoredReason = null,
    bool         CanStart           = true);

/// <summary>One of a tile's six bars: a check, coloured by its level, named in its tooltip.</summary>
/// <param name="Level">Unknown (grey) when there is no score to colour it by.</param>
public sealed record CheckBar(HealthIndicator Indicator, string Label, HealthLevel Level, string ToolTip);

/// <summary>
/// A connection as a Home tile shows it (#130): its status in words as well as colour, its score,
/// why the score is lower, a bar per check, and what its button does.
/// </summary>
public sealed record ServerTile(
    Guid                    Id,
    string                  Name,
    string                  ServerLine,
    bool                    IsActive,
    TileStatus              Status,
    HealthLevel             Level,
    string                  ScoreText,
    string                  Reason,
    string?                 ReasonToolTip,
    IReadOnlyList<CheckBar> Bars,
    string                  Footer,
    TileAction              Action,
    bool                    CanAct)
{
    /// <summary>The label beside the name: "CRITICAL", "UNREACHABLE", "OFF"…</summary>
    public string StatusLabel => Label(Status).ToUpperInvariant();

    /// <summary>"Open", "Retry" or "▶ Start monitoring".</summary>
    public string ActionText => Action switch
    {
        TileAction.Retry => "Retry",
        TileAction.Start => "▶ Start monitoring",
        _                => "Open",
    };

    /// <summary>What a screen reader says for the tile: "UAT, critical, score 70 of 100".</summary>
    public string AutomationName => Status switch
    {
        TileStatus.Off     => $"{Name}, not monitored",
        TileStatus.Waiting => $"{Name}, waiting for the first sample",
        _                  => $"{Name}, {Label(Status).ToLowerInvariant()}, score {ScoreText} of {HealthScore.Max}",
    } + (IsActive ? ", the active connection" : string.Empty);

    /// <summary>Its place in the sort: unreachable and critical first, not monitored last.</summary>
    public int Rank => Status switch
    {
        TileStatus.Unreachable or TileStatus.Critical => 0,
        TileStatus.Warning                            => 1,
        TileStatus.Healthy                            => 2,
        TileStatus.Waiting                            => 3,
        _                                             => 4,
    };

    public static string Label(TileStatus status) => status switch
    {
        TileStatus.Unreachable => "Unreachable",
        TileStatus.Critical    => "Critical",
        TileStatus.Warning     => "Warning",
        TileStatus.Healthy     => "Healthy",
        TileStatus.Waiting     => "Waiting",
        _                      => "Off",
    };

    /// <summary>The bar labels, short enough for six across a tile.</summary>
    public static string ShortName(HealthIndicator indicator) => indicator switch
    {
        HealthIndicator.Cpu                 => "CPU",
        HealthIndicator.Blocking            => "Block",
        HealthIndicator.PageLifeExpectancy  => "PLE",
        HealthIndicator.MemoryGrantsPending => "Grants",
        HealthIndicator.LogSpace            => "Log",
        HealthIndicator.TempDbSpace         => "TempDB",
        _                                   => indicator.ToString(),
    };

    public static TileStatus StatusOf(TileInput input) => input switch
    {
        { IsMonitored: false }                           => TileStatus.Off,
        { Score: null }                                  => TileStatus.Waiting,
        { Score.Level: HealthLevel.Unavailable }         => TileStatus.Unreachable,
        { Score.Level: HealthLevel.Critical }            => TileStatus.Critical,
        { Score.Level: HealthLevel.Warning }             => TileStatus.Warning,
        _                                                => TileStatus.Healthy,
    };

    /// <param name="now">This PC's clock, so a time from another day shows its date.</param>
    public static ServerTile From(TileInput input, DateTime now)
    {
        var status = StatusOf(input);
        var score  = status is TileStatus.Off or TileStatus.Waiting ? null : input.Score;
        var role   = input.IsActive ? "active" : "background";

        var (reason, toolTip) = status switch
        {
            TileStatus.Off         => ("Not monitored, so there's no score yet.", input.NotMonitoredReason),
            TileStatus.Waiting     => ("Waiting for the first sample…", null),
            TileStatus.Unreachable => (input.FailingSince is { } since
                                           ? $"Can't be reached or read since {When(since, now)}."
                                           : "Can't be reached or read.",
                                       input.LastError ?? score?.Problem),
            _ when score!.Lowering.Count == 0
                                   => ("Every check is within its thresholds.", null),
            _                      => ($"Lowered by {score.LoweredBy()}.", null),
        };

        var footer = status switch
        {
            TileStatus.Off         => input.NotMonitoredReason ?? "Not monitored.",
            TileStatus.Waiting     => $"Monitoring · {role}",
            TileStatus.Unreachable => input.LastSuccess is { } good ? $"Last good sample {When(good, now)}" : "No sample has worked yet",
            _                      => $"Sample {When(score!.Time, now)} · {role}",
        };

        var action = status switch
        {
            TileStatus.Off         => TileAction.Start,
            TileStatus.Unreachable => TileAction.Retry,
            _                      => TileAction.Open,
        };

        return new ServerTile(
            input.Id,
            input.Name,
            $"{input.Server.Trim()} / {input.Database}",
            input.IsActive,
            status,
            score?.Level ?? HealthLevel.Unknown,
            score?.Value.ToString(CultureInfo.CurrentCulture) ?? "—",
            reason,
            toolTip,
            BarsFor(score, status),
            footer,
            action,
            CanAct: action != TileAction.Start || input.CanStart);
    }

    // A bar per check, in the order of the Health score popup; grey with no score to colour them.
    private static IReadOnlyList<CheckBar> BarsFor(HealthScore? score, TileStatus status)
    {
        if (score is { Checks.Count: > 0 })
            return score.Checks.Select(c => new CheckBar(
                c.Indicator, ShortName(c.Indicator), c.Level,
                $"{c.Name}: {c.ValueText} (warning {c.WarningText}, critical {c.CriticalText})" +
                (c.PointsLost > 0 ? $" · −{c.PointsLost}" : string.Empty))).ToList();

        var why = status switch
        {
            TileStatus.Unreachable => "the server can't be reached",
            TileStatus.Waiting     => "waiting for the first sample",
            _                      => "not monitored",
        };
        return Enum.GetValues<HealthIndicator>()
                   .Select(i => new CheckBar(i, ShortName(i), HealthLevel.Unknown, $"{HealthThresholds.Info(i).Name}: no figure, {why}"))
                   .ToList();
    }

    // "21:55:14" today, "30 Sep 21:55" on another day.
    internal static string When(DateTime time, DateTime now) =>
        time.Date == now.Date
            ? time.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : time.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>The order of Home's tiles (#130).</summary>
public static class TileOrder
{
    /// <summary>
    /// Most attention needed first: unreachable and critical, then warning, healthy, waiting and
    /// not monitored; by name within each. The active connection is sorted like any other.
    /// </summary>
    public static IReadOnlyList<ServerTile> Sort(IEnumerable<ServerTile> tiles) =>
        tiles.OrderBy(t => t.Rank)
             .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
             .ThenBy(t => t.Id)
             .ToList();
}

/// <summary>The line and counts at the top of Home (#130).</summary>
public sealed record HomeSummary(int Connections, int Monitored, int Unreachable, int Critical, int Warning, int Healthy)
{
    public static HomeSummary From(IReadOnlyCollection<ServerTile> tiles) => new(
        tiles.Count,
        tiles.Count(t => t.Status != TileStatus.Off),
        tiles.Count(t => t.Status == TileStatus.Unreachable),
        tiles.Count(t => t.Status == TileStatus.Critical),
        tiles.Count(t => t.Status == TileStatus.Warning),
        tiles.Count(t => t.Status == TileStatus.Healthy));

    /// <summary>"5 connections · 3 monitored · sorted by most attention needed".</summary>
    public string Headline =>
        (Connections == 1 ? "1 connection" : $"{Connections} connections") +
        $" · {Monitored} monitored" +
        (Connections > 1 ? " · sorted by most attention needed" : string.Empty);

    /// <summary>A count per status that has any, worst first: ("2 critical", Critical)…</summary>
    public IReadOnlyList<(string Text, TileStatus Status)> Counts =>
        new[]
        {
            (Unreachable, TileStatus.Unreachable),
            (Critical,    TileStatus.Critical),
            (Warning,     TileStatus.Warning),
            (Healthy,     TileStatus.Healthy),
        }
        .Where(c => c.Item1 > 0)
        .Select(c => ($"{c.Item1} {ServerTile.Label(c.Item2).ToLowerInvariant()}", c.Item2))
        .ToList();
}
