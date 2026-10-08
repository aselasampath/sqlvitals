using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Wait statistics (#47).</summary>
public interface IWaitsRepository
{
    Task<IEnumerable<WaitStatCumulative>> GetCumulativeWaitsAsync(CancellationToken cancellationToken = default);

    Task<IEnumerable<ActiveWait>> GetActiveWaitsAsync(CancellationToken cancellationToken = default);
    Task<SignalVsResourceWait?> GetSignalVsResourceAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<TopWaitType>> GetTopWaitTypesAsync(CancellationToken cancellationToken = default);
}
