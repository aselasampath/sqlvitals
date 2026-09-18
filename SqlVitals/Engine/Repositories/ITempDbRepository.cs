using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

public interface ITempDbRepository
{
    Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync();
}
