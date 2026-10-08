using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Wait statistics (#47).</summary>
public interface IWaitsRepository
{
    Task<IEnumerable<WaitStatCumulative>> GetCumulativeWaitsAsync();

    Task<IEnumerable<ActiveWait>> GetActiveWaitsAsync();
    Task<SignalVsResourceWait?> GetSignalVsResourceAsync();
    Task<IEnumerable<TopWaitType>> GetTopWaitTypesAsync();
}
