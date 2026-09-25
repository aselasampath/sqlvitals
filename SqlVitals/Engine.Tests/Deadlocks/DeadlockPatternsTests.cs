using SqlVitals.Engine.Deadlocks;

namespace SqlVitals.Engine.Tests.Deadlocks;

public class DeadlockPatternsTests
{
    private static DeadlockReport Report(DateTime utc, string[] objects, params string?[] procedures) =>
        Report(utc, objects, procedures.Select(p => (p, (string?)null)).ToArray());

    private static DeadlockReport Report(DateTime utc, string[] objects, (string? Procedure, string? Statement)[] code) =>
        new(utc,
            code.Select((c, i) => new DeadlockProcess(
                $"p{i}", i == 0, 50 + i, "app", null, null, "Sales", null, null, null, 0, 1, null,
                c.Procedure, c.Statement, null, null)).ToList(),
            objects.Select(o => new DeadlockResource("Key", o, "PK", "", [], [])).ToList(),
            "<deadlock />");

    private static readonly DateTime T0 = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Groups_deadlocks_on_the_same_objects_by_the_same_code_most_frequent_first()
    {
        var reports = new[]
        {
            Report(T0,              ["dbo.Orders", "dbo.Lines"], "dbo.usp_Ship", "dbo.usp_Pick"),
            Report(T0.AddHours(1),  ["dbo.Stock"],               "dbo.usp_Count"),
            // The same pair, listed the other way round.
            Report(T0.AddHours(2),  ["dbo.Lines", "dbo.Orders"], "dbo.usp_Pick", "dbo.usp_Ship"),
            Report(T0.AddHours(3),  ["dbo.Orders", "dbo.Lines"], "dbo.usp_Ship", "dbo.usp_Pick"),
        };

        var (patterns, all) = DeadlockPatterns.Group(reports);

        Assert.Equal(2, patterns.Count);
        var top = patterns[0];
        Assert.Equal(3, top.Count);
        Assert.Equal("dbo.Lines; dbo.Orders", top.Objects);
        Assert.Equal(T0.AddHours(3).ToLocalTime(), top.LastSeen);
        Assert.Equal(T0.ToLocalTime(), top.FirstSeen);
        Assert.Same(top.Deadlocks[0], top.Newest);
        Assert.Equal(1, patterns[1].Count);

        // Every report, newest first, knows how often its pattern was seen.
        Assert.Equal([T0.AddHours(3), T0.AddHours(2), T0.AddHours(1), T0], all.Select(r => r.TimestampUtc));
        Assert.Equal([3, 3, 1, 3], all.Select(r => r.TimesSeen));
    }

    [Fact]
    public void Treats_the_same_ad_hoc_statement_with_other_values_as_the_same_code()
    {
        var reports = new[]
        {
            Report(T0,             ["dbo.Orders"], [(null, "UPDATE dbo.Orders SET Status = 'A' WHERE OrderId = 42")]),
            Report(T0.AddHours(1), ["dbo.Orders"], [(null, "update  dbo.Orders SET Status = N'B'\r\n WHERE OrderId = 7")]),
            Report(T0.AddHours(2), ["dbo.Orders"], [(null, "DELETE dbo.Orders WHERE OrderId = 7")]),
        };

        var (patterns, _) = DeadlockPatterns.Group(reports);

        Assert.Equal([2, 1], patterns.Select(p => p.Count));
    }

    [Fact]
    public void A_different_object_is_a_different_pattern()
    {
        var (patterns, _) = DeadlockPatterns.Group(
        [
            Report(T0,             ["dbo.Orders"], "dbo.usp_Ship"),
            Report(T0.AddHours(1), ["dbo.Invoices"], "dbo.usp_Ship"),
        ]);

        Assert.Equal(2, patterns.Count);
        // Equal counts: the most recent first.
        Assert.Equal("dbo.Invoices", patterns[0].Objects);
    }

    [Fact]
    public void No_deadlocks_no_patterns()
    {
        var (patterns, all) = DeadlockPatterns.Group([]);

        Assert.Empty(patterns);
        Assert.Empty(all);
    }

    [Theory]
    [InlineData("UPDATE t SET a = 'x''y' WHERE id = 10", "UPDATE T SET A = ? WHERE ID = ?")]
    [InlineData("SELECT * FROM t2 WHERE v = 1.5 OR h = 0x1F", "SELECT * FROM T2 WHERE V = ? OR H = ?")]
    [InlineData("EXEC p @p1 = N'abc',   @p2 = 3", "EXEC P @P1 = ?, @P2 = ?")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Normalizes_statements_without_literals_or_spacing(string? statement, string expected)
    {
        Assert.Equal(expected, DeadlockPatterns.Normalize(statement));
    }
}
