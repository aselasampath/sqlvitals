using System.Diagnostics;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>SP Trace: an Extended Events session made, read and dropped on the server.</summary>
[Collection(SqlServerTestGroup.Name)]
public class SpTraceTests(SqlServerFixture server)
{
    [SqlServerFact]
    [Covers(nameof(ISpTraceRepository.StartTraceAsync), nameof(ISpTraceRepository.PollTraceEventsAsync),
            nameof(ISpTraceRepository.PollProcedureStatsAsync), nameof(ISpTraceRepository.GetProcedureStatsTotalsAsync),
            nameof(ISpTraceRepository.GetTraceStatusAsync), nameof(ISpTraceRepository.StopTraceAsync))]
    public async Task Trace_CapturesTheProcedureCalls_ThenDropsItsSession()
    {
        var repo = server.NewRepository();

        var started = await repo.StartTraceAsync(SpTraceOptions.Default);
        try
        {
            Assert.True(started.IsRunning);
            Assert.True(started.Mode == TraceMode.ExtendedEvents, started.Reason);

            await server.RunProceduresAsync(5);

            // The session hands events to its ring buffer within its 2-second dispatch latency.
            var events = new List<SpTraceEvent>();
            var clock = Stopwatch.StartNew();
            while (events.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                events.AddRange(await repo.PollTraceEventsAsync());
            }
            Assert.Contains(events, e => e.ObjectName is { } name && name.Contains("GetCustomerOrders", StringComparison.Ordinal));

            Assert.Contains(await repo.PollProcedureStatsAsync(), r => r.ProcedureName == "GetCustomerOrders");
            Assert.Contains(await repo.GetProcedureStatsTotalsAsync(), r => r.ProcedureName == "GetCustomerOrders");

            var status = await repo.GetTraceStatusAsync();
            Assert.True(status.IsRunning);
            Assert.True(status.EventsCaptured > 0);
        }
        finally
        {
            Assert.False((await repo.StopTraceAsync()).IsRunning);
        }
    }

    [SqlServerFact]
    public async Task NestedCalls_SayWhatCalledThem_AndTheirLevel()
    {
        var repo = server.NewRepository();

        var started = await repo.StartTraceAsync(SpTraceOptions.Default);
        try
        {
            Assert.True(started.Mode == TraceMode.ExtendedEvents, started.Reason);

            await server.RunNestedProceduresAsync();

            // The RPC ends last: once it is read, so are the calls it made.
            var events = new List<SpTraceEvent>();
            var clock = Stopwatch.StartNew();
            while (!events.Exists(IsOuterRpc) && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                events.AddRange(await repo.PollTraceEventsAsync());
            }
            var rpc = events.Find(IsOuterRpc);
            Assert.True(rpc is not null, Describe(events));

            // The calls of that request, and what called each.
            var request = SpTraceNesting.ReadActivity(rpc.ActivityId)?.Request;
            Assert.True(request is not null, $"no activity id: {Describe(events)}");
            var calls = events.Where(e => SpTraceNesting.ReadActivity(e.ActivityId)?.Request == request).ToList();
            Assert.All(calls, c => Assert.NotNull(SpTraceNesting.ReadActivity(c.ActivityId)?.Sequence));
            var nesting = SpTraceNesting.Resolve(calls);
            var rows = calls.Select((c, i) => (c.EventName, Name: Last(c.ObjectName), nesting[i].CalledBy, nesting[i].Level)).ToList();
            var described = string.Join(Environment.NewLine, rows) + Environment.NewLine + Describe(calls);

            Assert.True(rows.Count == 6, described);
            Assert.True(rows.Count(r => r is { EventName: "rpc_completed", Name: "NestOuter", CalledBy: CallNesting.Client, Level: 1 }) == 1, described);
            Assert.True(rows.Count(r => r is { EventName: "module_end", Name: "NestOuter", CalledBy: CallNesting.Client, Level: 1 }) == 1, described);
            Assert.True(rows.Count(r => r is { Name: "NestMiddle", CalledBy: "NestOuter", Level: 2 }) == 2, described);
            Assert.True(rows.Count(r => r is { Name: "NestInner", CalledBy: "NestMiddle", Level: 3 }) == 2, described);
            Assert.True(nesting.Count(n => n.IsTopLevel) == 1, described);   // Top-level only: the RPC, once
        }
        finally
        {
            Assert.False((await repo.StopTraceAsync()).IsRunning);
        }

        static bool IsOuterRpc(SpTraceEvent e) => e.EventName == "rpc_completed" && Last(e.ObjectName) == "NestOuter";

        static string? Last(string? name) => name?[(name.LastIndexOf('.') + 1)..];

        static string Describe(IEnumerable<SpTraceEvent> events) => string.Join(Environment.NewLine,
            events.Select(e => $"{e.EventName} {e.ObjectName} {e.EventTime:HH:mm:ss.fffffff} {e.DurationUs} us {e.ActivityId}"));
    }
}
