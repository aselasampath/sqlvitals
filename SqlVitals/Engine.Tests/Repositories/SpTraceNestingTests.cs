using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

/// <summary>Live Calls' Called by and Level (#238): each call's caller, from its request's activity id and its span.</summary>
public class SpTraceNestingTests
{
    private const string Request = "5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F";

    private static readonly DateTime T0 = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Local);

    // A call that began startMs into the request and ran for durationMs, sequence seq in its request.
    private static SpTraceEvent Call(string? name, double startMs, double durationMs, int seq, string ev = "module_end",
                                     string? request = Request) =>
        new(T0.AddMilliseconds(Math.Floor(startMs + durationMs)),   // XE timestamps are whole milliseconds
            ev, name, ev == "module_end" ? "P" : null, "SalesDb", (long)durationMs, 0, 0, 0, 0, 0, 61, "app", "OrderService", "WEB01",
            null, request is null ? null : $"{request}-{seq}")
        {
            DurationUs = (long)(durationMs * 1000),
        };

    private static SpTraceEvent Rpc(string name, double startMs, double durationMs, int seq, string? request = Request) =>
        Call(name, startMs, durationMs, seq, "rpc_completed", request);

    private static IReadOnlyList<CallNesting> Resolve(params SpTraceEvent[] events) => SpTraceNesting.Resolve(events);

    [Fact]
    public void AToBToC_EachIsCalledByTheOneAbove_OneLevelDeeper()
    {
        // dbo.A (RPC) calls dbo.B, which calls dbo.C. Each ends before its caller.
        var c   = Call("C", 5, 10, 1);
        var b   = Call("B", 3, 15, 2);
        var a   = Call("A", 0.1, 20, 3);
        var rpc = Rpc("dbo.A", 0, 20.3, 4);

        var nesting = Resolve(rpc, a, b, c);

        Assert.Equal(new CallNesting(CallNesting.Client, 1), nesting[0]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1, RepeatsRpc: true), nesting[1]);
        Assert.Equal(new CallNesting("A", 2), nesting[2]);
        Assert.Equal(new CallNesting("B", 3), nesting[3]);
        Assert.Equal([true, false, false, false], nesting.Select(n => n.IsTopLevel));
    }

    [Fact]
    public void TheRpcAndItsModuleEnd_AreOneCall_NeitherCallsTheOther()
    {
        var nesting = Resolve(Call("usp_GetOrders", 0.2, 40, 1), Rpc("dbo.usp_GetOrders", 0, 40.5, 2));

        Assert.Equal(new CallNesting(CallNesting.Client, 1, RepeatsRpc: true), nesting[0]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1), nesting[1]);
        Assert.All(nesting, n => Assert.False(n.IsNested));

        // Top-level only keeps one of them, the rpc_completed row: the same call isn't counted twice.
        Assert.Equal([false, true], nesting.Select(n => n.IsTopLevel));
    }

    [Fact]
    public void AnRpcThatNamedNoProcedure_IsStillOneCallWithItsModuleEnd_AndNamedByIt()
    {
        var nesting = Resolve(Call("B", 5, 3, 1), Call("usp_A", 0.2, 20, 2), Call(null!, 0, 20.4, 3, "rpc_completed"));

        Assert.Equal(new CallNesting("usp_A", 2), nesting[0]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1, RepeatsRpc: true), nesting[1]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1), nesting[2]);
    }

    [Fact]
    public void SpResetConnection_InTheSameRequest_IsTheClients_NotCalledByTheRpc()
    {
        // As SQL Server 2022 sent it: the pool's reset (7 us) is sequence 1 of the request, and it
        // ends in the millisecond the 61 ms RPC's span begins.
        var reset = new SpTraceEvent(T0.AddMilliseconds(435), "rpc_completed", "sp_reset_connection", null, "SalesDb", 0, 0, 0, 0, 0, 0,
                                     61, "app", "OrderService", "WEB01", null, $"{Request}-1") { DurationUs = 7 };
        var outer = new SpTraceEvent(T0.AddMilliseconds(496), "rpc_completed", "NestOuter", null, "SalesDb", 61, 0, 0, 0, 0, 0,
                                     61, "app", "OrderService", "WEB01", null, $"{Request}-7") { DurationUs = 61_114 };

        var nesting = Resolve(reset, outer);

        Assert.All(nesting, n => Assert.Equal(new CallNesting(CallNesting.Client, 1), n));
    }

    [Fact]
    public void TwoCallsOneAfterTheOther_AreSiblings_NotNested()
    {
        // A calls B, then C: C began after B ended, so it isn't B's.
        var nesting = Resolve(Call("B", 1, 4, 1), Call("C", 6, 7, 2), Call("A", 0.2, 14, 3), Rpc("A", 0, 14.4, 4));

        Assert.Equal(new CallNesting("A", 2), nesting[0]);
        Assert.Equal(new CallNesting("A", 2), nesting[1]);
    }

    [Fact]
    public void AShortCallRightBeforeTheNext_IsStillItsSibling()
    {
        // B takes 0.3 ms and C starts as B ends: the microsecond durations keep B out of C.
        var nesting = Resolve(Call("B", 2.1, 0.3, 1), Call("C", 2.45, 4.7, 2), Call("A", 0, 9, 3));

        Assert.Equal("A", nesting[0].CalledBy);
        Assert.Equal("A", nesting[1].CalledBy);
    }

    [Fact]
    public void ACallStartingAsItsCallerDoes_IsInside_ThoughItsStartReadsEarlier()
    {
        // A's first statement is EXEC B: B ends at 3.95 ms, read as 3, so it reads as starting at -0.9 ms;
        // A ends at 4.1, read as 4, so it reads as starting at -0.1.
        var nesting = Resolve(Call("B", 0.05, 3.9, 1), Call("A", 0.0, 4.1, 2));

        Assert.Equal(new CallNesting("A", 2), nesting[0]);
    }

    [Fact]
    public void TheSameProcedureCalledTwice_EachIsItsOwnCall()
    {
        // A calls B twice: two rows of B, both A's.
        var nesting = Resolve(Call("B", 1, 3, 1), Call("B", 5, 3, 2), Call("A", 0.2, 10, 3), Rpc("dbo.A", 0, 10.4, 4));

        Assert.Equal(new CallNesting("A", 2), nesting[0]);
        Assert.Equal(new CallNesting("A", 2), nesting[1]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1, RepeatsRpc: true), nesting[2]);
    }

    [Fact]
    public void ARecursiveCall_IsCalledByTheOuterOne_NotTheRpc()
    {
        // The client calls dbo.A, which calls itself: the outer module_end is the RPC's.
        var nesting = Resolve(Call("A", 2, 3, 1), Call("A", 0.2, 8, 2), Rpc("dbo.A", 0, 8.4, 3));

        Assert.Equal(new CallNesting("A", 2), nesting[0]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1, RepeatsRpc: true), nesting[1]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1), nesting[2]);
    }

    [Fact]
    public void TwoRequests_DontMix()
    {
        var other = "0F0E0D0C-0B0A-4908-8706-050403020100";
        var nesting = Resolve(Call("B", 1, 3, 1), Rpc("A", 0, 10, 2, other), Call("A", 0.2, 9, 2));

        Assert.Equal(new CallNesting("A", 2), nesting[0]);                   // its own request's A
        Assert.Equal(new CallNesting(CallNesting.Client, 1), nesting[1]);    // nothing of its own inside
    }

    [Fact]
    public void AMissingParent_LeavesCalledByEmpty_AndNothingFails()
    {
        // B's caller hasn't ended yet, or its event was lost; the request has no RPC.
        var nesting = Resolve(Call("B", 1, 3, 1));

        Assert.Null(nesting[0].CalledBy);
        Assert.Equal(1, nesting[0].Level);
        Assert.False(nesting[0].IsNested);
    }

    [Fact]
    public void AChildRead_BeforeItsCaller_GetsItOnceTheCallerIsRead()
    {
        var b = Call("B", 1, 3, 1);
        Assert.Null(Resolve(b)[0].CalledBy);

        var nesting = Resolve(b, Call("A", 0.2, 900, 2), Rpc("dbo.A", 0, 900.4, 3));

        Assert.Equal(new CallNesting("A", 2), nesting[0]);
    }

    [Fact]
    public void EventsWithNoActivityId_AreNotTied_ButAnRpcIsStillTheClients()
    {
        var nesting = Resolve(Call("B", 1, 3, 1, request: null), Rpc("A", 0, 10, 2, request: null));

        Assert.Equal(new CallNesting(null, null), nesting[0]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1), nesting[1]);
    }

    [Fact]
    public void ProceduresUnderSpExecutesql_AreCalledByIt()
    {
        // sp_executesql's own module_end isn't captured (it isn't a stored procedure): the RPC row is the caller.
        var nesting = Resolve(Call("usp_Inner", 1, 5, 1), Rpc("sp_executesql", 0, 7, 2));

        Assert.Equal(new CallNesting("sp_executesql", 2), nesting[0]);
    }

    [Fact]
    public void WithoutSequences_TheOrderTheyEndedIn_IsReadFromTheirTimes()
    {
        static SpTraceEvent Bare(SpTraceEvent e) => e with { ActivityId = Request };

        var nesting = Resolve(Bare(Rpc("dbo.A", 0, 20.3, 9)), Bare(Call("C", 5, 10, 9)), Bare(Call("A", 0.1, 20, 9)), Bare(Call("B", 3, 15, 9)));

        Assert.Equal(new CallNesting(CallNesting.Client, 1), nesting[0]);
        Assert.Equal(new CallNesting("B", 3), nesting[1]);
        Assert.Equal(new CallNesting(CallNesting.Client, 1, RepeatsRpc: true), nesting[2]);
        Assert.Equal(new CallNesting("A", 2), nesting[3]);
    }

    [Theory]
    [InlineData("5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F-12", "5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F", 12L)]
    [InlineData("5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F:3", "5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F", 3L)]
    [InlineData("{5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F}-3", "5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F", 3L)]
    [InlineData("5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F", "5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F", null)]
    [InlineData("{5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F}", "5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F", null)]   // one request, with or without braces
    [InlineData("A1B2C3D4-0001", "A1B2C3D4", 1L)]
    public void TheActivityId_IsReadAsRequestAndSequence(string id, string request, long? sequence) =>
        Assert.Equal((request, sequence), SpTraceNesting.ReadActivity(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoActivityId_IsNoRequest(string? id) => Assert.Null(SpTraceNesting.ReadActivity(id));

    [Fact]
    public void FiveThousandCalls_InOneRequest_AreEachTiedToTheirOwnCaller()
    {
        // A batch looping over a procedure: one request, thousands of siblings, each with a child.
        var events = new List<SpTraceEvent>();
        for (int i = 0; i < 2_500; i++)
        {
            events.Add(Call("Inner", i * 10 + 1, 2, i * 2 + 1));
            events.Add(Call("Outer", i * 10, 5, i * 2 + 2));
        }

        var nesting = SpTraceNesting.Resolve(events);

        Assert.All(nesting.Where((_, i) => i % 2 == 0), n => Assert.Equal(new CallNesting("Outer", 2), n));
        Assert.All(nesting.Where((_, i) => i % 2 == 1), n => Assert.Equal(new CallNesting(null, 1), n));
    }
}
