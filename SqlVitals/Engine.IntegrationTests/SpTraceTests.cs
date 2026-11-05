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
            // The pool's sp_reset_connection comes in the same request, before the call: the client's too.
            var calls = events.Where(e => SpTraceNesting.ReadActivity(e.ActivityId)?.Request == request).ToList();
            Assert.All(calls, c => Assert.NotNull(SpTraceNesting.ReadActivity(c.ActivityId)?.Sequence));
            var nesting = SpTraceNesting.Resolve(calls);
            var rows = calls.Select((c, i) => (c.EventName, Name: Last(c.ObjectName), nesting[i].CalledBy, nesting[i].Level)).ToList();
            var described = string.Join(Environment.NewLine, rows) + Environment.NewLine + Describe(calls) + Environment.NewLine +
                await server.DescribeAsync(
                    "SELECT e.name, e.predicate FROM sys.server_event_session_events e " +
                    "JOIN sys.server_event_sessions s ON s.event_session_id = e.event_session_id WHERE s.name = N'SqlVitals_SpTrace'") +
                Environment.NewLine +
                await server.DescribeAsync("SELECT map_key, map_value FROM sys.dm_xe_map_values WHERE name = N'object_type' AND map_key IN (8272, 20816, 17232)") +
                Environment.NewLine +
                await server.DescribeAsync(
                    @"SELECT CAST(t.target_data AS xml).value('count(/RingBufferTarget/event[@name=""module_end""])', 'int') AS ModuleEnds, " +
                    @"CAST(t.target_data AS xml).value('(/RingBufferTarget/event[@name=""module_end""]/data[@name=""object_type""]/text)[1]', 'nvarchar(60)') AS ObjectTypeText, " +
                    @"CAST(CAST(t.target_data AS xml).query('(/RingBufferTarget/event[@name=""module_end""]/data[@name=""object_type""])[1]') AS nvarchar(max)) AS ObjectTypeXml " +
                    "FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address WHERE s.name = N'SqlVitals_SpTrace'");

            Assert.True(rows.Count(r => r.Name != "sp_reset_connection") == 6, described);
            Assert.All(rows.Where(r => r.Name == "sp_reset_connection"), r => Assert.True(r is { CalledBy: CallNesting.Client, Level: 1 }, described));
            Assert.True(rows.Count(r => r is { EventName: "rpc_completed", Name: "NestOuter", CalledBy: CallNesting.Client, Level: 1 }) == 1, described);
            Assert.True(rows.Count(r => r is { EventName: "module_end", Name: "NestOuter", CalledBy: CallNesting.Client, Level: 1 }) == 1, described);
            Assert.True(rows.Count(r => r is { Name: "NestMiddle", CalledBy: "NestOuter", Level: 2 }) == 2, described);
            Assert.True(rows.Count(r => r is { Name: "NestInner", CalledBy: "NestMiddle", Level: 3 }) == 2, described);
            Assert.True(rows.Where((r, i) => nesting[i].IsTopLevel && r.Name == "NestOuter").Count() == 1, described);   // Top-level only: the RPC, once
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
