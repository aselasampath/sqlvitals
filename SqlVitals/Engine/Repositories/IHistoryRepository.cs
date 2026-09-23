using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>The server reads behind the local monitoring history (see Engine/History).</summary>
public interface IHistoryRepository
{
    /// <summary>
    /// Reads cumulative wait, file I/O, query and memory counters. With <paramref name="queriesExecutedSince"/>
    /// null every cached query_hash is returned (the baseline); otherwise only those executed since then.
    /// Either way at most <paramref name="maxQueries"/>, highest total CPU first.
    /// </summary>
    Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries);

    /// <summary>Statement text for the given query hashes ("0x…"), cut to <paramref name="maxLength"/> characters.</summary>
    Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength);
}
