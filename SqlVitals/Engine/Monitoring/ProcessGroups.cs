using System.Globalization;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Monitoring;

/// <summary>
/// The sessions of one login (or, later, one host, program or database) in the Processes grid (#116),
/// with the totals its group header shows.
/// </summary>
/// <param name="Key">Identifies the group across refreshes: the name in upper case, or empty for <see cref="ProcessGroups.NoLogin"/>.</param>
/// <param name="Name">The name as shown: the spelling most of its sessions use.</param>
/// <param name="Running">Sessions with a request: every status but sleeping, dormant and preconnect.</param>
/// <param name="OpenTran">Sessions with an open transaction.</param>
public sealed record ProcessGroup(
    string Key,
    string Name,
    IReadOnlyList<ProcessNode> Sessions,
    int Running,
    int Sleeping,
    int Blocked,
    int HeadBlockers,
    long CpuMs,
    int OpenTran)
{
    public int Count => Sessions.Count;

    /// <summary>
    /// For example <c>142 sessions (3 running, 139 sleeping) · 2 blocked · 1 head blocker · CPU 12,340 ms · 5 open tran</c>.
    /// Parts that are zero are left out.
    /// </summary>
    public string Summary(IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        string N(long value) => value.ToString("N0", provider);

        var states = new List<string>();
        if (Running  > 0) states.Add($"{N(Running)} running");
        if (Sleeping > 0) states.Add($"{N(Sleeping)} sleeping");

        var parts = new List<string>
        {
            $"{N(Count)} {(Count == 1 ? "session" : "sessions")}" + (states.Count > 0 ? $" ({string.Join(", ", states)})" : ""),
        };
        if (Blocked      > 0) parts.Add($"{N(Blocked)} blocked");
        if (HeadBlockers > 0) parts.Add($"{N(HeadBlockers)} {(HeadBlockers == 1 ? "head blocker" : "head blockers")}");
        if (CpuMs        > 0) parts.Add($"CPU {N(CpuMs)} ms");
        if (OpenTran     > 0) parts.Add($"{N(OpenTran)} open tran");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Groups a process snapshot for the Processes grid (#116), problems first: a login with 300
/// sleeping sessions is a pool leak, one holding every head blocker is the application to look at.
/// </summary>
public static class ProcessGroups
{
    /// <summary>The group of sessions with no login.</summary>
    public const string NoLogin = "(no login)";

    /// <summary>
    /// One group per login (<c>login_name</c>), ignoring case. Sessions must already be classified
    /// (<see cref="BlockingChains.Classify"/>), since the counts read <see cref="ProcessNode.VisualState"/>.
    /// </summary>
    public static IReadOnlyList<ProcessGroup> ByLogin(IEnumerable<ProcessNode> nodes) =>
        By(nodes, n => n.UserName, NoLogin);

    /// <summary>
    /// One group per value of <paramref name="keyOf"/>, ignoring case; a blank value is grouped under
    /// <paramref name="blankName"/>. Groups with a head blocker come first, then those with a blocked
    /// session, then the most sessions, then by name.
    /// </summary>
    public static IReadOnlyList<ProcessGroup> By(IEnumerable<ProcessNode> nodes, Func<ProcessNode, string?> keyOf, string blankName)
    {
        return nodes
            .GroupBy(n => KeyOf(keyOf(n)))
            .Select(g => Summarize(g.Key, g.Key.Length == 0 ? blankName : NameOf(g.Select(keyOf)), g.ToList()))
            .OrderByDescending(g => g.HeadBlockers > 0)
            .ThenByDescending(g => g.Blocked > 0)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The group key of a login: upper case, so logins differing only in case share it; empty when there is none.</summary>
    public static string KeyOf(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.ToUpperInvariant();

    // The spelling most sessions use; the first in ordinal order on a tie, so it doesn't flicker between refreshes.
    private static string NameOf(IEnumerable<string?> spellings) =>
        spellings
            .GroupBy(s => s!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;

    private static ProcessGroup Summarize(string key, string name, IReadOnlyList<ProcessNode> sessions) => new(
        key,
        name,
        sessions,
        Running:      sessions.Count(IsRunning),
        Sleeping:     sessions.Count(n => IsStatus(n, "sleeping")),
        Blocked:      sessions.Count(n => n.VisualState == BlockingChains.Blocked),
        HeadBlockers: sessions.Count(n => n.VisualState == BlockingChains.HeadBlocker),
        CpuMs:        sessions.Sum(n => n.CpuTime),
        OpenTran:     sessions.Count(n => n.OpenTran > 0));

    // A session status (sleeping, dormant, preconnect) means no request; anything else is a request's status.
    private static bool IsRunning(ProcessNode n) =>
        !IsStatus(n, "sleeping") && !IsStatus(n, "dormant") && !IsStatus(n, "preconnect");

    private static bool IsStatus(ProcessNode n, string status) =>
        string.Equals(n.Status?.Trim(), status, StringComparison.OrdinalIgnoreCase);
}
