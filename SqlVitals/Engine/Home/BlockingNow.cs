using System.Text.RegularExpressions;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Home;

/// <summary>
/// The biggest blocking chain on a server right now, in a few lines (#130): who is at its head,
/// how many sessions wait behind it and on what, and what the head is running.
/// </summary>
/// <param name="Who">"svc_import on APP02 (SalesImport)"; whatever of the login, host and program is known.</param>
/// <param name="WaitTypes">The commonest wait types of the sessions in the chain, e.g. "LCK_M_X".</param>
/// <param name="Statement">The head's statement on one line, cut short; null when it has none.</param>
/// <param name="HeadIsSleeping">The head is idle, typically holding an open transaction.</param>
/// <param name="OtherChains">How many more chains there are.</param>
public sealed record BlockingNow(
    int      HeadSessionId,
    string   Who,
    int      Waiting,
    string   WaitTypes,
    TimeSpan LongestWait,
    string?  Statement,
    bool     HeadIsSleeping,
    int      OtherChains)
{
    /// <summary>The longest statement shown.</summary>
    public const int MaxStatementLength = 160;

    /// <summary>"3 sessions on LCK_M_X", "1 session".</summary>
    public string WaitingText =>
        (Waiting == 1 ? "1 session" : $"{Waiting} sessions") + (WaitTypes.Length > 0 ? $" on {WaitTypes}" : string.Empty);

    /// <summary>The biggest chain in a snapshot of the server's sessions; null when nothing is blocked.</summary>
    public static BlockingNow? From(IEnumerable<ProcessNode> nodes)
    {
        var sessions = BlockingChains.Classify(nodes);
        var chains   = BlockingChains.Find(sessions);
        if (chains.Count == 0)
            return null;

        var chain  = chains[0];
        var byId   = sessions.ToDictionary(n => n.SessionId);
        var forest = BlockingChains.Forest(sessions);
        var head   = byId[chain.HeadSessionId];

        var waiting = new List<ProcessNode>();
        void Walk(int id)
        {
            foreach (var kid in forest.ChildrenOf(id))
            {
                waiting.Add(byId[kid]);
                Walk(kid);
            }
        }
        Walk(head.SessionId);

        var waitTypes = waiting
            .Select(n => n.WaitType)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .GroupBy(w => w!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(2)
            .Select(g => g.Key);

        return new BlockingNow(
            head.SessionId,
            Describe(head),
            chain.BlockedCount,
            string.Join(", ", waitTypes),
            TimeSpan.FromMilliseconds(chain.LongestWaitMs),
            OneLine(head.CommandText),
            string.Equals(head.Status, "sleeping", StringComparison.OrdinalIgnoreCase),
            chains.Count - 1);
    }

    private static string Describe(ProcessNode n)
    {
        var who = string.IsNullOrWhiteSpace(n.UserName) ? "Unknown login" : n.UserName.Trim();
        if (!string.IsNullOrWhiteSpace(n.HostName))
            who += $" on {n.HostName.Trim()}";
        if (!string.IsNullOrWhiteSpace(n.ProgramName))
            who += $" ({n.ProgramName.Trim()})";
        return who;
    }

    // The statement with its whitespace collapsed, cut at a word where it can be.
    internal static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = Regex.Replace(text, @"\s+", " ").Trim();
        if (line.Length <= MaxStatementLength)
            return line;

        var cut   = line[..MaxStatementLength];
        var space = cut.LastIndexOf(' ');
        return (space > MaxStatementLength / 2 ? cut[..space] : cut).TrimEnd() + " …";
    }
}
