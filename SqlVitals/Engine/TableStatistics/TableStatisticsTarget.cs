namespace SqlVitals.Engine.TableStatistics;

/// <summary>
/// Where a link opens Table Statistics (#244): a table, by name since an object id means nothing
/// outside its database, and the statistics object when the link knows it.
/// </summary>
/// <param name="Database">The database; null for the connection's.</param>
/// <param name="Statistics">The statistics object; null to open on the table alone.</param>
/// <param name="FromAnotherServer">It came from a plan of a server other than the one open now, so it isn't looked for here.</param>
public sealed record TableStatisticsTarget(string? Database, string Schema, string Table, string? Statistics = null, bool FromAnotherServer = false)
{
    public string FullName => $"{Schema}.{Table}";

    /// <summary>
    /// The item with this name: exactly, as a case-sensitive database can hold both Orders and ORDERS,
    /// else ignoring case, as a plan's or a link's name may differ from the catalog's.
    /// </summary>
    public static T? Named<T>(IEnumerable<T> items, Func<T, string> name, string wanted) where T : class
    {
        var list = items as IReadOnlyList<T> ?? items.ToList();
        return list.FirstOrDefault(i => string.Equals(name(i), wanted, StringComparison.Ordinal))
            ?? list.FirstOrDefault(i => string.Equals(name(i), wanted, StringComparison.OrdinalIgnoreCase));
    }
}
