using System.Globalization;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Monitoring;

public class ProcessGroupsTests
{
    private static ProcessNode Node(int spid, string? login, int blockedBy = 0, string status = "running",
                                    long cpu = 0, int openTran = 0) =>
        new(spid, blockedBy, status, login, "host", "app", blockedBy != 0 ? "LCK_M_X" : null, 0,
            null, null, "db", cpu, 0, 0, DateTime.MinValue, DateTime.MinValue, openTran, null, null, "");

    private static IReadOnlyList<ProcessGroup> Groups(params ProcessNode[] nodes) =>
        ProcessGroups.ByLogin(BlockingChains.Classify(nodes));

    [Fact]
    public void CountsSessionsByState()
    {
        // 51 (sleeping, open tran) blocks 52; 53 runs; 54 sleeps. All one login.
        var g = Assert.Single(Groups(
            Node(51, "app", status: "sleeping", openTran: 1),
            Node(52, "app", blockedBy: 51, status: "suspended"),
            Node(53, "app"),
            Node(54, "app", status: "sleeping")));

        Assert.Equal(4, g.Count);
        Assert.Equal(2, g.Running);        // suspended and running both have a request
        Assert.Equal(2, g.Sleeping);
        Assert.Equal(1, g.Blocked);
        Assert.Equal(1, g.HeadBlockers);
        Assert.Equal(1, g.OpenTran);
    }

    [Fact]
    public void DormantAndPreconnect_AreNeitherRunningNorSleeping()
    {
        var g = Assert.Single(Groups(Node(51, "app", status: "dormant"), Node(52, "app", status: "preconnect")));

        Assert.Equal((2, 0, 0), (g.Count, g.Running, g.Sleeping));
        Assert.Equal("2 sessions", g.Summary(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Totals_AddUpCpu_AndCountSessionsWithAnOpenTransaction()
    {
        var g = Assert.Single(Groups(
            Node(51, "app", cpu: 12_000, openTran: 2),   // nested: still one session in a transaction
            Node(52, "app", cpu: 340,    openTran: 1),
            Node(53, "app", cpu: 0)));

        Assert.Equal(12_340, g.CpuMs);
        Assert.Equal(2, g.OpenTran);
    }

    [Fact]
    public void Summary_ShowsEveryPart()
    {
        var g = Assert.Single(Groups(
            Node(51, "app", status: "sleeping", cpu: 12_000, openTran: 1),
            Node(52, "app", blockedBy: 51, status: "suspended", cpu: 340),
            Node(53, "app", blockedBy: 51, status: "suspended")));

        Assert.Equal("3 sessions (2 running, 1 sleeping) · 2 blocked · 1 head blocker · CPU 12,340 ms · 1 open tran",
                     g.Summary(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Summary_LeavesOutZeroParts_AndSaysSessionForOne()
    {
        Assert.Equal("1 session (1 sleeping)",
                     Assert.Single(Groups(Node(51, "app", status: "sleeping"))).Summary(CultureInfo.InvariantCulture));
        Assert.Equal("1 session (1 running) · CPU 5 ms",
                     Assert.Single(Groups(Node(51, "app", cpu: 5))).Summary(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Summary_FormatsNumbersInTheCulture()
    {
        var g = Assert.Single(Groups(Node(51, "app", cpu: 12_340)));

        Assert.Contains("CPU 12 340 ms", g.Summary(new CultureInfo("fr-FR")).Replace(' ', ' ').Replace(' ', ' '));
    }

    [Fact]
    public void LoginsDifferingOnlyInCase_AreOneGroup_NamedAsMostSessionsSpellIt()
    {
        var g = Assert.Single(Groups(
            Node(51, @"CORP\svc-orders"),
            Node(52, @"corp\SVC-ORDERS"),
            Node(53, @"CORP\svc-orders")));

        Assert.Equal(3, g.Count);
        Assert.Equal(@"CORP\svc-orders", g.Name);
        Assert.Equal(@"CORP\SVC-ORDERS", g.Key);
    }

    [Fact]
    public void ATiedSpelling_IsChosenTheSameWayEveryTime()
    {
        var a = Assert.Single(Groups(Node(51, "sa"), Node(52, "SA")));
        var b = Assert.Single(Groups(Node(52, "SA"), Node(51, "sa")));

        Assert.Equal("SA", a.Name);   // ordinal: upper case sorts first
        Assert.Equal(a.Name, b.Name);
    }

    [Fact]
    public void SessionsWithNoLogin_AreGroupedUnderNoLogin()
    {
        var groups = Groups(Node(51, null), Node(52, ""), Node(53, "  "), Node(54, "sa"));

        var none = groups.Single(g => g.Name == ProcessGroups.NoLogin);
        Assert.Equal(3, none.Count);
        Assert.Equal("", none.Key);
    }

    [Fact]
    public void Groups_AreOrdered_HeadBlockerThenBlockedThenMostSessionsThenName()
    {
        var groups = Groups(
            Node(51, "big"),  Node(52, "big"),  Node(53, "big"),       // most sessions, no blocking
            Node(61, "zeta"), Node(62, "zeta"),                          // tie on count with "alpha": by name
            Node(63, "Alpha"), Node(64, "Alpha"),
            Node(71, "victim", blockedBy: 81),                           // has a blocked session
            Node(81, "holder", status: "sleeping"));                     // has the head blocker

        Assert.Equal(["holder", "victim", "big", "Alpha", "zeta"], groups.Select(g => g.Name));
    }

    [Fact]
    public void AChainThatCrossesLogins_CountsTheHeadInItsLoginAndTheBlockedInTheirs()
    {
        // The report job's session blocks two sessions of the orders service.
        var nodes = BlockingChains.Classify(
        [
            Node(51, "svc-orders", blockedBy: 90, status: "suspended"),
            Node(52, "svc-orders", blockedBy: 90, status: "suspended"),
            Node(53, "svc-orders", status: "sleeping"),
            Node(90, "report-job", status: "sleeping", openTran: 1),
        ]);

        var groups = ProcessGroups.ByLogin(nodes);

        Assert.Equal(["report-job", "svc-orders"], groups.Select(g => g.Name));
        Assert.Equal((1, 0), (groups[0].HeadBlockers, groups[0].Blocked));
        Assert.Equal((0, 2), (groups[1].HeadBlockers, groups[1].Blocked));

        // The grid's "Head of chain" for the blocked sessions is in the other group.
        var heads = BlockingChains.Heads(nodes);
        Assert.Equal(90, heads[51]);
        Assert.Equal(90, heads[52]);
        Assert.Equal(53, heads[53]);
    }

    [Fact]
    public void By_GroupsOnAnyKey()
    {
        var groups = ProcessGroups.By(BlockingChains.Classify([Node(51, "a"), Node(52, "b")]), n => n.HostName, "(no host)");

        var g = Assert.Single(groups);
        Assert.Equal(("host", 2), (g.Name, g.Count));
    }

    [Fact]
    public void AnEmptySnapshot_HasNoGroups()
    {
        Assert.Empty(ProcessGroups.ByLogin([]));
    }
}
