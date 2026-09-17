using SqlPulse.Engine.Models;

namespace SqlPulse.Engine.Repositories;

public interface ITempDbRepository
{
    Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync();
}
