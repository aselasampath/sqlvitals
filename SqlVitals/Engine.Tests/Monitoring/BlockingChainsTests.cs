using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Monitoring;

public class BlockingChainsTests
{
    private static ProcessNode Node(int spid, int blockedBy = 0, string status = "running", long waitMs = 0) =>
        new(spid, blockedBy, status, "login", "host", "app", blockedBy != 0 ? "LCK_M_X" : null, waitMs,
            null, null, "db", 0, 0, 0, DateTime.MinValue, DateTime.MinValue, 0, null, null, "");

    private static string State(IReadOnlyList<ProcessNode> nodes, int spid) =>
        nodes.Single(n => n.SessionId == spid).VisualState;

    // 55 (sleeping, open transaction) blocks 60 and 61; 61 blocks 70. 80 is idle, 90 is running.
    private static IReadOnlyList<ProcessNode> TypicalChain() => BlockingChains.Classify(
    [
        Node(90),
        Node(70, blockedBy: 61, waitMs: 4_000),
        Node(61, blockedBy: 55, waitMs: 9_000),
        Node(80, status: "sleeping"),
        Node(55, status: "sleeping"),
        Node(60, blockedBy: 55, waitMs: 12_000),
    ]);

    [Fact]
    public void Classify_MarksTheHeadBlocker()
    {
        // The head was never marked, so the legend's orange "Head Blocker" never appeared.
        var nodes = TypicalChain();

        Assert.Equal(BlockingChains.HeadBlocker, State(nodes, 55));
        Assert.Equal(BlockingChains.Blocked,     State(nodes, 60));
        Assert.Equal(BlockingChains.Blocked,     State(nodes, 61));   // blocks 70 but is itself blocked
        Assert.Equal(BlockingChains.Blocked,     State(nodes, 70));
        Assert.Equal(BlockingChains.Sleeping,    State(nodes, 80));
        Assert.Equal(BlockingChains.Active,      State(nodes, 90));
    }

    [Fact]
    public void Classify_IgnoresASessionBlockedByItself()
    {
        // Parallel queries on older builds report blocking_session_id = session_id.
        var nodes = BlockingChains.Classify([Node(52, blockedBy: 52)]);

        Assert.Equal(0, nodes[0].ParentId);
        Assert.Equal(BlockingChains.Active, nodes[0].VisualState);
        Assert.Empty(BlockingChains.Find(nodes));
    }

    [Fact]
    public void Classify_KeepsOneRowPerSession_TheBlockedOne()
    {
        var nodes = BlockingChains.Classify([Node(52), Node(52, blockedBy: 51), Node(51)]);

        Assert.Equal(2, nodes.Count);
        Assert.Equal(51, nodes.Single(n => n.SessionId == 52).ParentId);
        Assert.Equal(BlockingChains.HeadBlocker, State(nodes, 51));
    }

    [Fact]
    public void Classify_ANegativeBlockerIsBlockedWithoutAHead()
    {
        // -2: an orphaned distributed transaction. Nothing on the map to point at.
        var nodes = BlockingChains.Classify([Node(52, blockedBy: -2)]);

        Assert.Equal(BlockingChains.Blocked, nodes[0].VisualState);
        Assert.Contains(52, BlockingChains.InChains(nodes));
    }

    [Fact]
    public void Find_ReportsEachChainFromItsHead()
    {
        var chain = Assert.Single(BlockingChains.Find(TypicalChain()));

        Assert.Equal(55, chain.HeadSessionId);
        Assert.Equal(3, chain.BlockedCount);
        Assert.Equal(2, chain.Depth);
        Assert.Equal(12_000, chain.LongestWaitMs);
    }

    [Fact]
    public void Find_LargestChainFirst()
    {
        var nodes = BlockingChains.Classify(
        [
            Node(10), Node(11, blockedBy: 10),
            Node(20), Node(21, blockedBy: 20), Node(22, blockedBy: 20),
        ]);

        Assert.Equal([20, 10], BlockingChains.Find(nodes).Select(c => c.HeadSessionId));
    }

    [Fact]
    public void Forest_PutsSessionsUnderTheirBlocker_ChainsFirst()
    {
        var forest = BlockingChains.Forest(TypicalChain());

        Assert.Equal([55, 80, 90], forest.Roots);
        Assert.Equal([60, 61], forest.ChildrenOf(55));
        Assert.Equal([70],     forest.ChildrenOf(61));
        Assert.Empty(forest.ChildrenOf(70));
    }

    [Fact]
    public void Forest_ABlockerMissingFromTheSnapshot_LeavesTheBlockedSessionAsARoot()
    {
        var forest = BlockingChains.Forest(BlockingChains.Classify([Node(52, blockedBy: 999)]));

        Assert.Equal([52], forest.Roots);
    }

    [Fact]
    public void Forest_ACycleIsCutAtTheLowestSpid_AndIsNotReportedAsAChain()
    {
        // A deadlock the deadlock monitor hasn't resolved yet: 40 waits on 41, 41 on 40, 42 on 41.
        var nodes  = BlockingChains.Classify([Node(41, blockedBy: 40), Node(40, blockedBy: 41), Node(42, blockedBy: 41)]);
        var forest = BlockingChains.Forest(nodes);

        Assert.Equal([40], forest.Roots);
        Assert.Equal([41], forest.ChildrenOf(40));
        Assert.Equal([42], forest.ChildrenOf(41));
        Assert.Empty(BlockingChains.Find(nodes));
        Assert.Equal(new HashSet<int> { 40, 41, 42 }, BlockingChains.ChainOf(nodes, 42));
    }

    [Fact]
    public void InChains_LeavesOutSessionsNotBlockingOrBlocked()
    {
        Assert.Equal(new HashSet<int> { 55, 60, 61, 70 }, BlockingChains.InChains(TypicalChain()));
    }

    [Fact]
    public void HeadOf_WalksUpToTheHead()
    {
        var nodes = TypicalChain();

        Assert.Equal(55, BlockingChains.HeadOf(nodes, 70));
        Assert.Equal(55, BlockingChains.HeadOf(nodes, 55));
        Assert.Equal(90, BlockingChains.HeadOf(nodes, 90));
    }

    [Fact]
    public void Heads_IsHeadOfForEverySession()
    {
        var nodes = TypicalChain();
        var heads = BlockingChains.Heads(nodes);

        Assert.Equal(nodes.Count, heads.Count);
        foreach (var n in nodes)
            Assert.Equal(BlockingChains.HeadOf(nodes, n.SessionId), heads[n.SessionId]);
    }

    [Fact]
    public void Heads_StopsOnACycle()
    {
        var heads = BlockingChains.Heads(BlockingChains.Classify([Node(41, blockedBy: 40), Node(40, blockedBy: 41)]));

        Assert.Equal(2, heads.Count);   // returned at all: the walk didn't loop forever
    }

    [Fact]
    public void BlockedDirectly_CountsOnlyTheSessionsRightUnderEach()
    {
        var counts = BlockingChains.BlockedDirectly(TypicalChain());

        Assert.Equal(2, counts[55]);    // 60 and 61, not 70
        Assert.Equal(1, counts[61]);
        Assert.False(counts.ContainsKey(90));
    }

    [Fact]
    public void Visible_ChainsOnly_KeepsTheChains()
    {
        Assert.Equal([70, 61, 55, 60], BlockingChains.Visible(TypicalChain(), chainsOnly: true, spidSearch: null).Select(n => n.SessionId));
    }

    [Fact]
    public void Visible_ASearch_KeepsTheWholeChainOfAMatch()
    {
        // 70 matches "70"; its chain brings its head 55 and 55's other victim 60.
        Assert.Equal([70, 61, 55, 60], BlockingChains.Visible(TypicalChain(), chainsOnly: false, spidSearch: " 70 ").Select(n => n.SessionId));
        Assert.Equal([80],             BlockingChains.Visible(TypicalChain(), chainsOnly: false, spidSearch: "80").Select(n => n.SessionId));
        Assert.Equal(6,                BlockingChains.Visible(TypicalChain(), chainsOnly: false, spidSearch: "").Count);
    }

    [Fact]
    public void ChainOf_IsTheWholeTree()
    {
        var nodes = TypicalChain();

        Assert.Equal(new HashSet<int> { 55, 60, 61, 70 }, BlockingChains.ChainOf(nodes, 60));
        Assert.Equal(new HashSet<int> { 80 },             BlockingChains.ChainOf(nodes, 80));
    }

    [Theory]
    [InlineData(55, "SPID 55")]
    [InlineData(-2, "an orphaned distributed transaction (-2)")]
    [InlineData(-9, "an unidentified blocker (-9)")]
    public void DescribeBlocker(int blockingSessionId, string expected) =>
        Assert.Equal(expected, BlockingChains.DescribeBlocker(blockingSessionId));

    [Fact]
    public void ProcessesSql_ReadsWhatASleepingHeadBlockerLeaves()
    {
        var sql = SessionRepository.ProcessesSql;

        // No request: the connection's last batch, and the session's own open transactions.
        Assert.Contains("most_recent_sql_handle", sql);
        Assert.Contains("s.open_transaction_count", sql);
        // A system session at the head of a chain is not a user process.
        Assert.Contains("OR s.session_id IN (SELECT blocking_session_id", sql);
    }

    // ── A blocking alert's evidence (#145) ───────────────────────────

    [Fact]
    public void EvidenceHead_IsTheChainWaitingLongest_WhatTheAlertMeasures()
    {
        // 10 blocks two sessions briefly; 20 blocks one for a minute.
        var chains = BlockingChains.Find(BlockingChains.Classify(
        [
            Node(10), Node(11, blockedBy: 10, waitMs: 2_000), Node(12, blockedBy: 10, waitMs: 3_000),
            Node(20), Node(21, blockedBy: 20, waitMs: 60_000),
        ]));

        Assert.Equal(20, BlockingChains.EvidenceHead(chains, pinned: null));
    }

    [Fact]
    public void EvidenceHead_StaysOnItsChain_WhileItLasts_ThenMovesOn()
    {
        var both = BlockingChains.Find(BlockingChains.Classify(
        [
            Node(10), Node(11, blockedBy: 10, waitMs: 90_000),
            Node(20), Node(21, blockedBy: 20, waitMs: 1_000),
        ]));
        var onlyTwenty = BlockingChains.Find(BlockingChains.Classify([Node(20), Node(21, blockedBy: 20, waitMs: 2_000)]));

        // Pinned on 20 after the first look: 10 waiting longer later doesn't move it.
        Assert.Equal(20, BlockingChains.EvidenceHead(both, pinned: 20));
        // 10's chain has ended: the one left.
        Assert.Equal(20, BlockingChains.EvidenceHead(onlyTwenty, pinned: 10));
        Assert.Null(BlockingChains.EvidenceHead([], pinned: 10));
    }
}
