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
}
