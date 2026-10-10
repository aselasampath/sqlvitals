namespace SqlVitals.Collector.Tests;

/// <summary>The collector's own thread (#172): work in order, and nothing thrown at a late caller.</summary>
public sealed class SerialContextTests
{
    [Fact]
    public async Task Work_RunsInOrder_OnOneThread()
    {
        using var serial = new SerialContext("test", (_, _) => { });
        var seen = new List<(int Step, int Thread)>();

        for (var i = 0; i < 20; i++)
        {
            var step = i;
            serial.Post(_ => seen.Add((step, Environment.CurrentManagedThreadId)), null);
        }
        await serial.RunAsync(() => { });

        Assert.Equal(Enumerable.Range(0, 20), seen.Select(s => s.Step));
        Assert.Single(seen.Select(s => s.Thread).Distinct());
    }

    [Fact]
    public async Task AFailingItem_IsReported_AndTheNextStillRuns()
    {
        var errors = new List<string>();
        using var serial = new SerialContext("test", (message, _) => errors.Add(message));

        serial.Post(_ => throw new InvalidOperationException("boom"), null);
        await serial.RunAsync(() => { });

        Assert.Single(errors);
    }

    [Fact]
    public async Task AfterDispose_APostIsDropped_AndRunAsyncFails()
    {
        var serial = new SerialContext("test", (_, _) => { });
        serial.Dispose();

        serial.Post(_ => throw new InvalidOperationException("never runs"), null);   // must not throw
        await Assert.ThrowsAsync<ObjectDisposedException>(() => serial.RunAsync(() => { }));
        serial.Dispose();
    }
}
