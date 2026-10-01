using SqlVitals.Engine.Home;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Tests.Home;

/// <summary>The biggest blocking chain in a few lines, for the server detail page (#130).</summary>
public sealed class BlockingNowTests
{
    private static ProcessNode Node(int spid, int blockedBy = 0, string status = "running", long waitMs = 0,
                                    string? wait = null, string? login = "app", string? host = "APP01", string? program = null,
                                    string? text = null) =>
        new(spid, blockedBy, status, login, host, program, wait, waitMs, null, null, "Sales", 0, 0, 0,
            DateTime.MinValue, DateTime.MinValue, 0, null, text, string.Empty);

    [Fact]
    public void NothingBlocked_IsNull()
    {
        Assert.Null(BlockingNow.From([Node(51), Node(52, status: "sleeping")]));
        Assert.Null(BlockingNow.From([]));
    }

    [Fact]
    public void TheBiggestChain_ItsHead_WhoIsWaiting_OnWhat_AndForHowLong()
    {
        var nodes = new[]
        {
            Node(87, status: "sleeping", login: "svc_import", host: "APP02", program: "SalesImport",
                 text: "UPDATE dbo.Orders\r\n   SET Status = @s\r\n WHERE Id = @id"),
            Node(90, blockedBy: 87, waitMs: 192_000, wait: "LCK_M_X"),
            Node(91, blockedBy: 87, waitMs: 60_000,  wait: "LCK_M_X"),
            Node(92, blockedBy: 90, waitMs: 30_000,  wait: "LCK_M_S"),
            // A smaller chain elsewhere.
            Node(60), Node(61, blockedBy: 60, waitMs: 5_000, wait: "LCK_M_U"),
        };

        var now = BlockingNow.From(nodes)!;

        Assert.Equal(87, now.HeadSessionId);
        Assert.Equal("svc_import on APP02 (SalesImport)", now.Who);
        Assert.Equal(3, now.Waiting);
        Assert.Equal("LCK_M_X, LCK_M_S", now.WaitTypes);
        Assert.Equal("3 sessions on LCK_M_X, LCK_M_S", now.WaitingText);
        Assert.Equal(TimeSpan.FromSeconds(192), now.LongestWait);
        Assert.Equal("UPDATE dbo.Orders SET Status = @s WHERE Id = @id", now.Statement);
        Assert.True(now.HeadIsSleeping);
        Assert.Equal(1, now.OtherChains);
    }

    [Fact]
    public void AHeadWithNoStatementOrLogin_StillDescribesWhatIsKnown()
    {
        var now = BlockingNow.From([Node(55, login: null, host: null), Node(56, blockedBy: 55, waitMs: 1_000)])!;

        Assert.Equal("Unknown login", now.Who);
        Assert.Null(now.Statement);
        Assert.Equal("1 session", now.WaitingText);
        Assert.False(now.HeadIsSleeping);
        Assert.Equal(0, now.OtherChains);
    }

    [Fact]
    public void ALongStatement_IsCutAtAWord()
    {
        var text = string.Join(" ", Enumerable.Repeat("SELECT column_name", 30));

        var line = BlockingNow.OneLine(text)!;

        Assert.True(line.Length <= BlockingNow.MaxStatementLength + 2);
        Assert.EndsWith(" …", line);
        Assert.DoesNotContain("column_nam …", line);
    }
}
