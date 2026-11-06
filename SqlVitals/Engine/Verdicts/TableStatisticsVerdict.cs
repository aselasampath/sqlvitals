using System.Globalization;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Engine.Verdicts;

/// <summary>
/// Table Statistics' verdict (#242): the largest table and how much of it its indexes take, tables
/// under a schema change, and what the login couldn't read.
/// </summary>
public static class TableStatisticsVerdict
{
    /// <summary>Selects the largest table.</summary>
    public const string ShowLargest = "show-largest";

    /// <summary>The table the verdict names: the first of a list that's largest first, if its size is known.</summary>
    public static TableSize? Largest(IEnumerable<TableSize> tables) =>
        tables.Where(t => t.TotalPages is not null).MaxBy(t => t.TotalPages);

    public static PageVerdict From(string database, IReadOnlyList<TableSize> tables, IReadOnlyList<TableReadFailure> failures)
    {
        var culture = CultureInfo.CurrentCulture;
        var notRead = string.Concat(failures.Where(f => f.What != TableReadFailure.Sizes).Select(f => " " + Describe(f)));

        if (failures.FirstOrDefault(f => f.What == TableReadFailure.Sizes) is { } sizes)
            return new PageVerdict(VerdictLevel.Warning, $"Couldn't read the table sizes in {database}",
                $"The tables are listed without them. {Describe(sizes)}{notRead}");

        if (tables.Count == 0)
            return failures.Any(f => f.What == TableReadFailure.Names)
                ? new PageVerdict(VerdictLevel.Warning, $"This login can't see any tables in {database}", notRead.Trim())
                : new PageVerdict(VerdictLevel.Ok, $"{database} has no user tables", notRead);

        var locked = tables.Count(t => t.IsLocked);
        var lockedText = locked == 0 ? "" : string.Format(culture,
            "{0} under a schema change, shown as Locked and left out of these figures: Refresh reads {1} again. ",
            Sizes.Plural(locked, "table is", "tables are"), locked == 1 ? "it" : "them");

        if (Largest(tables) is not { } largest)
            return new PageVerdict(VerdictLevel.Warning, string.Format(culture, "{0} in {1} under a schema change", Sizes.Plural(locked, "table is", "tables are"), database),
                lockedText + notRead);

        var size = TableSpace.SizeText(largest.TotalPages);
        var sized = tables.Count - locked;
        var someHidden = failures.Any(f => f.What == TableReadFailure.Names);
        var headline = locked > 0 || someHidden ? $"{largest.FullName} is the largest table read in {database}: {size}"
            : sized == 1 ? $"{largest.FullName} is the only table in {database}: {size}"
            : string.Format(culture, "{0} is the largest of {1:N0} tables in {2}: {3}", largest.FullName, sized, database, size);
        var share = largest.TotalPages > 0 ? 100.0 * largest.IndexPages!.Value / largest.TotalPages!.Value : 0;
        var why = string.Format(culture, "{0}Its indexes take {1}, {2:0.#} % of it. The tables read reserve {3} together.{4}",
            lockedText, TableSpace.SizeText(largest.IndexPages), share, TableSpace.SizeText(tables.Sum(t => t.TotalPages ?? 0)), notRead);
        return new PageVerdict(locked > 0 || failures.Count > 0 ? VerdictLevel.Warning : VerdictLevel.Info, headline, why,
                               [new(ShowLargest, "Show it")]);
    }

    /// <summary>"Not read: index reads and writes. The login lacks VIEW SERVER STATE. A DBA can grant it once: …"</summary>
    public static string Describe(TableReadFailure failure) =>
        $"Not read: {failure.What}. {failure.Explanation}" +
        (failure.FixScript is { } fix ? $" A DBA can grant it once:\n{fix}\n" : "");
}
