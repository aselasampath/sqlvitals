using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Monitoring;

/// <summary>
/// One live blocking chain: the head blocker and every session waiting behind it, directly or
/// through another blocked session.
/// </summary>
/// <param name="HeadSessionId">The session at the root of the chain. Nothing it waits on is a visible session.</param>
/// <param name="BlockedCount">Sessions waiting behind the head, at any depth.</param>
/// <param name="Depth">Longest path from the head: 1 when everything waits on the head directly.</param>
/// <param name="LongestWaitMs">The longest current wait among the blocked sessions.</param>
public sealed record BlockingChain(int HeadSessionId, int BlockedCount, int Depth, long LongestWaitMs);

/// <summary>
/// The sessions of a process map as a forest: every session hangs under the session blocking it
/// (<c>dm_exec_requests.blocking_session_id</c>). Always acyclic, so it can be walked without guards.
/// </summary>
/// <param name="Roots">Chains first, the largest first; then the sessions not in a chain, by SPID.</param>
/// <param name="Children">The sessions directly under each session, by SPID. Leaves have no entry.</param>
public sealed record ProcessForest(IReadOnlyList<int> Roots, IReadOnlyDictionary<int, IReadOnlyList<int>> Children)
{
    public IReadOnlyList<int> ChildrenOf(int sessionId) =>
        Children.TryGetValue(sessionId, out var kids) ? kids : [];
}

/// <summary>
/// Builds blocking chains from a process snapshot (#37), so the head blocker is found at once
/// rather than by following "blocked by" numbers.
/// </summary>
public static class BlockingChains
{
    public const string Active      = "Active";
    public const string HeadBlocker = "HeadBlocker";
    public const string Blocked     = "Blocked";
    public const string Sleeping    = "Sleeping";

    /// <summary>
    /// Cleans the snapshot and sets each session's <see cref="ProcessNode.VisualState"/>:
    /// <c>Blocked</c> when it waits on anything, <c>HeadBlocker</c> when it blocks others and waits
    /// on nothing, otherwise <c>Sleeping</c> or <c>Active</c> from its status.
    /// </summary>
    public static IReadOnlyList<ProcessNode> Classify(IEnumerable<ProcessNode> nodes)
    {
        var list = nodes
            // Parallel queries on older builds report the session as blocked by itself: not a chain.
            .Select(n => n.ParentId == n.SessionId ? n with { ParentId = 0 } : n)
            // A MARS session can have several requests; keep one, the blocked one if any.
            .GroupBy(n => n.SessionId)
            .Select(g => g.OrderByDescending(n => n.ParentId != 0).First())
            .ToList();

        var blockers = list.Where(n => n.ParentId > 0).Select(n => n.ParentId).ToHashSet();

        return list.Select(n => n with { VisualState = StateOf(n, blockers.Contains(n.SessionId)) }).ToList();
    }

    private static string StateOf(ProcessNode n, bool blocksOthers) =>
        n.ParentId != 0 ? Blocked
        : blocksOthers ? HeadBlocker
        : string.Equals(n.Status, "sleeping", StringComparison.OrdinalIgnoreCase) ? Sleeping
        : Active;

    /// <summary>
    /// Arranges the sessions as trees under their blockers. A session whose blocker isn't in the
    /// snapshot (or is a negative "blocked by" code) is a root. Sessions blocking each other in a
    /// circle (a deadlock the deadlock monitor hasn't resolved yet) are hung under the lowest SPID.
    /// </summary>
    public static ProcessForest Forest(IReadOnlyList<ProcessNode> nodes)
    {
        var ids = nodes.Select(n => n.SessionId).ToHashSet();

        var edges = nodes
            .Where(n => n.ParentId != n.SessionId && ids.Contains(n.ParentId))
            .GroupBy(n => n.ParentId)
            .ToDictionary(g => g.Key, g => g.Select(n => n.SessionId).Distinct().OrderBy(id => id).ToList());

        var visited  = new HashSet<int>();
        var children = new Dictionary<int, IReadOnlyList<int>>();

        int Visit(int id)
        {
            visited.Add(id);
            int size = 1;
            if (!edges.TryGetValue(id, out var kids)) return size;

            var treeKids = new List<int>();
            foreach (var kid in kids)
            {
                if (visited.Contains(kid)) continue;
                treeKids.Add(kid);
                size += Visit(kid);
            }
            if (treeKids.Count > 0) children[id] = treeKids;
            return size;
        }

        var sized = new List<(int Id, int Size)>();
        foreach (var root in nodes
                     .Where(n => n.ParentId == n.SessionId || !ids.Contains(n.ParentId))
                     .Select(n => n.SessionId).Distinct().OrderBy(id => id))
        {
            if (!visited.Contains(root)) sized.Add((root, Visit(root)));
        }

        // Whatever is left sits on a cycle, or under one.
        foreach (var id in ids.OrderBy(id => id))
        {
            if (!visited.Contains(id)) sized.Add((id, Visit(id)));
        }

        var roots = sized
            .OrderByDescending(r => r.Size > 1)
            .ThenByDescending(r => r.Size)
            .ThenBy(r => r.Id)
            .Select(r => r.Id)
            .ToList();

        return new ProcessForest(roots, children);
    }

    /// <summary>The live chains, the most sessions blocked first.</summary>
    public static IReadOnlyList<BlockingChain> Find(IReadOnlyList<ProcessNode> nodes)
    {
        var forest = Forest(nodes);
        var byId   = ById(nodes);
        var chains = new List<BlockingChain>();

        foreach (var root in forest.Roots)
        {
            if (forest.ChildrenOf(root).Count == 0) continue;
            // A root on a deadlock cycle is only where the circle was cut, not a head.
            if (byId[root].ParentId > 0 && byId.ContainsKey(byId[root].ParentId)) continue;

            int count = 0, depth = 0;
            long longestWait = 0;
            void Walk(int id, int level)
            {
                foreach (var kid in forest.ChildrenOf(id))
                {
                    count++;
                    depth       = Math.Max(depth, level + 1);
                    longestWait = Math.Max(longestWait, byId[kid].WaitTimeMs);
                    Walk(kid, level + 1);
                }
            }
            Walk(root, 0);
            chains.Add(new BlockingChain(root, count, depth, longestWait));
        }

        return chains
            .OrderByDescending(c => c.BlockedCount)
            .ThenBy(c => c.HeadSessionId)
            .ToList();
    }

    /// <summary>Sessions that block, or are blocked by, anything.</summary>
    public static IReadOnlySet<int> InChains(IReadOnlyList<ProcessNode> nodes)
    {
        var blockers = nodes.Where(n => n.ParentId > 0 && n.ParentId != n.SessionId).Select(n => n.ParentId).ToHashSet();
        return nodes
            .Where(n => (n.ParentId != 0 && n.ParentId != n.SessionId) || blockers.Contains(n.SessionId))
            .Select(n => n.SessionId)
            .ToHashSet();
    }

    /// <summary>The session at the top of <paramref name="sessionId"/>'s chain; the session itself when nothing visible blocks it.</summary>
    public static int HeadOf(IReadOnlyList<ProcessNode> nodes, int sessionId)
    {
        var byId = ById(nodes);
        var seen = new HashSet<int>();
        int id   = sessionId;
        while (seen.Add(id) && byId.TryGetValue(id, out var n) && n.ParentId != id && byId.ContainsKey(n.ParentId))
            id = n.ParentId;
        return id;
    }

    /// <summary>Every session in the same tree as <paramref name="sessionId"/>: its head and everything under it.</summary>
    public static IReadOnlySet<int> ChainOf(IReadOnlyList<ProcessNode> nodes, int sessionId)
    {
        var forest = Forest(nodes);
        int top    = forest.Roots.FirstOrDefault(r => Contains(forest, r, sessionId), sessionId);
        var result = new HashSet<int>();
        void Walk(int id)
        {
            result.Add(id);
            foreach (var kid in forest.ChildrenOf(id)) Walk(kid);
        }
        Walk(top);
        return result;
    }

    private static bool Contains(ProcessForest forest, int root, int id) =>
        root == id || forest.ChildrenOf(root).Any(kid => Contains(forest, kid, id));

    /// <summary>
    /// What a "blocked by" value means: <c>SPID n</c>, or SQL Server's description of a negative code
    /// (the blocker isn't a session).
    /// </summary>
    public static string DescribeBlocker(int blockingSessionId) => blockingSessionId switch
    {
        > 0 => $"SPID {blockingSessionId}",
        0   => "nothing",
        -2  => "an orphaned distributed transaction (-2)",
        -3  => "a deferred recovery transaction (-3)",
        -4  => "a latch owner SQL Server can't identify yet (-4)",
        -5  => "a latch owner SQL Server doesn't track (-5)",
        _   => $"an unidentified blocker ({blockingSessionId})",
    };

    private static Dictionary<int, ProcessNode> ById(IReadOnlyList<ProcessNode> nodes) =>
        nodes.GroupBy(n => n.SessionId).ToDictionary(g => g.Key, g => g.First());
}
