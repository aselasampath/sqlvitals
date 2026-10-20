using System.Globalization;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Monitoring;

/// <summary>What a Resource Queries tab ranks its object groups by (#232).</summary>
public enum QueryGroupRank { Reads, Cpu }

/// <summary>
/// The statements of one stored procedure, function or trigger in a Resource Queries grid (#232), or
/// the ad hoc queries, with the totals its group header shows. The totals are of the statements in
/// the list (the top N), not of every statement the object has.
/// </summary>
/// <param name="Key">Identifies the group across refreshes: the database and the object, in upper case; the database alone for ad hoc queries.</param>
/// <param name="Name">The object as shown, or <see cref="QueryObjectGroups.AdHoc"/>.</param>
/// <param name="Type">"Stored Proc", "Trigger"…; null for ad hoc queries.</param>
/// <param name="CpuUs">Total CPU (worker) time in microseconds, as the grid's CPU columns.</param>
public sealed record QueryObjectGroup(
    string  Key,
    string  Name,
    string? Type,
    int     Queries,
    long    Reads,
    long    CpuUs)
{
    /// <summary>For example <c>3 queries · 1,234,567 reads · CPU 12.3 s</c>.</summary>
    public string Summary(IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        return string.Join(" · ",
            $"{Queries.ToString("N0", provider)} {(Queries == 1 ? "query" : "queries")}",
            $"{Reads.ToString("N0", provider)} reads",
            $"CPU {Cpu(CpuUs, provider)}");
    }

    // Microseconds as the most readable unit: 850 µs, 12 ms, 12.3 s.
    private static string Cpu(long us, IFormatProvider provider) => us switch
    {
        < 1_000     => $"{us.ToString("N0", provider)} µs",
        < 1_000_000 => $"{(us / 1_000.0).ToString("N0", provider)} ms",
        _           => $"{(us / 1_000_000.0).ToString("N1", provider)} s",
    };
}

/// <summary>
/// Groups the Resource Queries grids by the object a statement belongs to (#232): a procedure with
/// ten statements in the list is one line to read, and its totals rank it against a single costly
/// ad hoc query.
/// </summary>
public static class QueryObjectGroups
{
    /// <summary>The group of statements that belong to no object.</summary>
    public const string AdHoc = "Ad hoc queries";

    /// <summary>The group a statement goes in; see <see cref="QueryObjectGroup.Key"/>.</summary>
    public static string KeyOf(ResourceIntensiveQuery query) =>
        $"{query.DatabaseName?.Trim().ToUpperInvariant()}\u001F{(IsAdHoc(query) ? "" : query.ObjectName!.Trim().ToUpperInvariant())}";

    /// <summary>
    /// One group per object, the costliest first by <paramref name="rank"/> (their statements'
    /// total reads or CPU), then by name. Ad hoc queries are one group, ranked the same way.
    /// </summary>
    public static IReadOnlyList<QueryObjectGroup> ByObject(IEnumerable<ResourceIntensiveQuery> queries, QueryGroupRank rank) =>
        queries
            .GroupBy(KeyOf)
            .Select(g =>
            {
                var first = g.First();
                return new QueryObjectGroup(
                    g.Key,
                    IsAdHoc(first) ? AdHoc : first.ObjectName!.Trim(),
                    IsAdHoc(first) ? null : first.ObjectType,
                    g.Count(),
                    g.Sum(q => q.TotalLogicalReads),
                    g.Sum(q => q.TotalCPUTime));
            })
            .OrderByDescending(g => rank == QueryGroupRank.Cpu ? g.CpuUs : g.Reads)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsAdHoc(ResourceIntensiveQuery query) => string.IsNullOrWhiteSpace(query.ObjectName);
}
