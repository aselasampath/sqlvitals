using System.Globalization;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Settings;

/// <summary>A connection's role, as the label on its card in Settings → Connections says it (#131).</summary>
public enum ConnectionRole
{
    /// <summary>The connection every page queries.</summary>
    Active,
    /// <summary>Monitored, while another connection is active.</summary>
    Background,
    /// <summary>Neither active nor monitored.</summary>
    Off,
}

/// <summary>Everything a connection's card needs; never a credential.</summary>
/// <param name="Database">The database label, e.g. "Sales" or "(default database)".</param>
/// <param name="SignIn">The sign-in type, short: "SQL login", "Entra MFA", "Windows auth".</param>
/// <param name="MonitorInBackground">Its Monitor in background setting.</param>
/// <param name="OwnThresholds">It has its own health thresholds.</param>
/// <param name="IsMonitored">Being monitored right now.</param>
/// <param name="Score">The latest score; null while not monitored or before the first sample.</param>
public sealed record ConnectionCardInput(
    Guid         Id,
    string       Name,
    string       Server,
    string       Database,
    string       SignIn,
    bool         IsActive,
    bool         MonitorInBackground,
    bool         OwnThresholds,
    bool         IsMonitored,
    HealthScore? Score);

/// <summary>
/// A saved connection as its card in Settings → Connections shows it (#131): its role, where it
/// points, its settings as tags, and its state in words.
/// </summary>
/// <param name="Tags">The sign-in type, the monitoring mode, and default or own thresholds.</param>
/// <param name="Status">As its Home tile (#130) has it, which colours the dot and the state.</param>
/// <param name="StateText">"Score 70 · Critical", "Unreachable · can't connect", "No score: not monitored".</param>
public sealed record ConnectionCard(
    Guid                  Id,
    string                Name,
    string                ServerLine,
    bool                  IsActive,
    ConnectionRole        Role,
    IReadOnlyList<string> Tags,
    TileStatus            Status,
    string                StateText)
{
    public const string DefaultThresholdsTag = "Default thresholds";
    public const string OwnThresholdsTag     = "Own thresholds";

    /// <summary>"ACTIVE", "BACKGROUND" or "OFF".</summary>
    public string RoleLabel => Role.ToString().ToUpperInvariant();

    /// <summary>What a screen reader says for the card: "UAT, background, score 70 · critical".</summary>
    public string AutomationName =>
        $"{Name}, {Role.ToString().ToLowerInvariant()}, {StateText.ToLower(CultureInfo.CurrentCulture)}";

    public static ConnectionCard From(ConnectionCardInput input)
    {
        // The same status as its Home tile, so the two never disagree.
        var status = ServerTile.StatusOf(new TileInput(input.Id, input.Name, input.Server, input.Database,
                                                       input.IsActive, input.IsMonitored, input.Score));
        var role = input.IsActive ? ConnectionRole.Active
                 : input.IsMonitored ? ConnectionRole.Background
                 : ConnectionRole.Off;

        var monitoring = !input.IsMonitored ? "Not monitored"
                       : input.IsActive ? "Monitored"
                       : input.MonitorInBackground ? "Monitor in background"
                       : "Started by you";

        var state = status switch
        {
            TileStatus.Off         => "No score: not monitored",
            TileStatus.Waiting     => "Waiting for the first sample",
            TileStatus.Unreachable => "Unreachable · can't connect",
            _                      => string.Format(CultureInfo.CurrentCulture, "Score {0} · {1}", input.Score!.Value, ServerTile.Label(status)),
        };

        return new ConnectionCard(
            input.Id,
            input.Name,
            $"{input.Server.Trim()} / {input.Database}",
            input.IsActive,
            role,
            [input.SignIn, monitoring, input.OwnThresholds ? OwnThresholdsTag : DefaultThresholdsTag],
            status,
            state);
    }

    /// <summary>The cards in the sidebar selector's order: by name.</summary>
    public static IReadOnlyList<ConnectionCard> Order(IEnumerable<ConnectionCard> cards) =>
        cards.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
             .ThenBy(c => c.Id)
             .ToList();
}
