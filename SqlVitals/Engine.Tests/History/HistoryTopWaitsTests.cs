using System.Text.RegularExpressions;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.History;

/// <summary>The Waits page's table over a past range (#178), from the history's buckets.</summary>
public sealed class HistoryTopWaitsTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static WaitHistoryBucket Bucket(int minute, params WaitTypeTotals[] waits) =>
        new(T0.AddMinutes(minute), 300, waits.ToDictionary(w => w.WaitType));

    [Fact]
    public void Over_SumsEachWaitType_RanksThem_AndSharesOutTheTotal()
    {
        var top = HistoryTopWaits.Over(
        [
            Bucket(0, new("WRITELOG", 10, 2_000, 200), new("CXPACKET", 5, 1_000, 0)),
            Bucket(5, new("WRITELOG", 30, 4_000, 400), new("LCK_M_X", 2, 3_000, 0)),
        ], T0.AddMinutes(10));

        Assert.Equal(["WRITELOG", "LCK_M_X", "CXPACKET"], top.Select(w => w.WaitType));
        var first = top[0];
        Assert.Equal((1, "I/O", 40L, 6_000L, 600L, 5_400L), (first.WaitRank, first.WaitCategory, first.WaitingTasksCount, first.WaitTimeMs, first.SignalWaitTimeMs, first.ResourceWaitTimeMs));
        Assert.Equal(6.0, first.WaitTimeSec);
        Assert.Equal(150.0, first.AvgWaitMsPerTask);
        Assert.Equal(60.0, first.PctOfTotal, 6);
        Assert.Equal(10.0, first.SignalWaitPct, 6);
        Assert.Equal("#1 WRITELOG", first.RankLabel);
        Assert.Equal(T0.AddMinutes(10), first.CaptureTime);

        Assert.Equal([60.0, 90.0, 100.0], top.Select(w => Math.Round(w.CumulativePct, 6)));
        Assert.Equal(["I/O", "Locks", "CPU"], top.Select(w => w.WaitCategory));
    }

    [Fact]
    public void Over_KeepsTheTop25_AndLeavesOutWaitTypesThatDidntWait()
    {
        var waits = Enumerable.Range(1, 30).Select(i => new WaitTypeTotals($"W{i:00}", 1, i, 0))
                              .Append(new WaitTypeTotals("IDLE", 3, 0, 0)).ToArray();

        var top = HistoryTopWaits.Over([Bucket(0, waits)], T0);

        Assert.Equal(HistoryTopWaits.Count, top.Count);
        Assert.Equal("W30", top[0].WaitType);
        Assert.DoesNotContain(top, w => w.WaitType == "IDLE");
        // The shares are of every wait type, not only the 25 shown.
        Assert.True(top[^1].CumulativePct < 100);
    }

    [Fact]
    public void Over_NothingRecorded_IsEmpty() =>
        Assert.Empty(HistoryTopWaits.Over([Bucket(0)], T0));

    [Theory]
    [InlineData("SOS_SCHEDULER_YIELD", "CPU")]
    [InlineData("PAGEIOLATCH_SH", "I/O")]
    [InlineData("RESOURCE_SEMAPHORE", "Memory")]
    [InlineData("LCK_M_IX", "Locks")]
    [InlineData("LOCK_HASH", "Locks")]
    [InlineData("ASYNC_NETWORK_IO", "Network")]
    [InlineData("HADR_SYNC_COMMIT", "Other")]
    [InlineData("pageiolatch_ex", "I/O")]
    public void WaitCategory_AsTheTopWaitsQueryGroupsThem(string waitType, string category) =>
        Assert.Equal(category, WaitCategory.Of(waitType));

    [Fact]
    public void WaitCategory_HasEveryWaitTypeOfTheTopWaitsQuery_InTheSameGroup()
    {
        // The live table's groups come from the query's CASE, a past range's from WaitCategory:
        // a wait type added to one and not the other would change groups with the range.
        var lists = Regex.Matches(SqlScripts.Get("Waits/TopWaitTypes"), @"WHEN f\.wait_type IN \(([^)]*)\) THEN '([^']+)'");
        Assert.Equal(4, lists.Count);   // CPU, I/O, Memory, Network; Locks is a LIKE

        var listed = lists.SelectMany(m => Regex.Matches(m.Groups[1].Value, "'([^']+)'").Select(t => (Type: t.Groups[1].Value, Category: m.Groups[2].Value))).ToList();
        Assert.True(listed.Count > 40, $"{listed.Count} wait types");
        Assert.All(listed, w => Assert.Equal(w.Category, WaitCategory.Of(w.Type)));
    }
}
