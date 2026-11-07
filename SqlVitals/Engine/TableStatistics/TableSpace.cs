using System.Globalization;

namespace SqlVitals.Engine.TableStatistics;

/// <summary>The databases Table Statistics can read (#242), and the one the connection points to.</summary>
/// <param name="CanSwitch">False on Azure SQL Database, where a connection stays in its database.</param>
public sealed record TableDatabases(string Connected, IReadOnlyList<string> Names, bool CanSwitch);

/// <summary>A read that failed while the rest of the page's still read: what's missing, and the fix.</summary>
/// <param name="What">What couldn't be read: "table sizes".</param>
/// <param name="Explanation">Why, naming the permission: "The login lacks VIEW DATABASE STATE in Sales."</param>
/// <param name="FixScript">The GRANT that fixes it; null when it isn't a permission.</param>
public sealed record TableReadFailure(string What, string Explanation, string? FixScript)
{
    /// <summary>The read the table list's sizes come from.</summary>
    public const string Sizes = "table sizes";

    /// <summary>The login may not see every table: the list holds those it can.</summary>
    public const string Names = "the names of every table";

    /// <summary>A table's statistics, which the login isn't allowed to read (#243, #244).</summary>
    public const string Statistics = "table statistics";

    /// <summary>What the GRANT offered with <see cref="Statistics"/> also allows: said wherever it's offered (#244).</summary>
    public const string SelectWarning = "Careful: SELECT also lets the login read the table's rows, even though SqlVitals doesn't. "
        + "db_ddladmin reads statistics without SELECT, but lets it change the schema. Sizes show either way, so you can also go without.";
}

/// <summary>A user table's space. Sizes are null while it's locked, or when the login can't read them.</summary>
/// <param name="DataPages">Reserved pages of its heap or clustered index, LOB and row-overflow included.</param>
/// <param name="IndexPages">Reserved pages of its other indexes.</param>
/// <param name="IsLocked">Under a schema change (<c>ALTER TABLE</c>, an offline rebuild, <c>TRUNCATE</c>).</param>
public sealed record TableSize(int ObjectId, string SchemaName, string TableName, long? RowsInTable, long? DataPages, long? IndexPages, bool IsLocked)
{
    public long? TotalPages => DataPages + IndexPages;

    public string FullName => SchemaName + "." + TableName;
}

/// <summary>A database's tables, the largest first, and what couldn't be read of them.</summary>
public sealed record TableSizeList(IReadOnlyList<TableSize> Tables, IReadOnlyList<TableReadFailure> Failures);

/// <summary>One of a table's indexes, or its heap. Sizes and usage are null when they couldn't be read.</summary>
/// <param name="Name">Null for a heap.</param>
/// <param name="Type">As <c>sys.indexes</c> says: "NONCLUSTERED".</param>
/// <param name="IsUnique">Unique, a primary key or a unique constraint: it enforces a rule.</param>
/// <param name="Reads">Seeks, scans and lookups since the server restarted.</param>
/// <param name="Writes">Updates since the server restarted.</param>
public sealed record TableIndexSize(int IndexId, string? Name, string Type, bool IsUnique, long? RowsInIndex,
                                    long? ReservedPages, long? UsedPages, long? Reads, long? Writes)
{
    /// <summary>The heap or clustered index: the table's data, not an index on top of it.</summary>
    public bool IsData => IndexId <= 1;
}

/// <summary>A table's indexes, the largest first.</summary>
/// <param name="IsLocked">The table was under a schema change, so its sizes couldn't be read.</param>
/// <param name="ServerStarted">When the server restarted, which resets the usage counts; null when it couldn't be read.</param>
public sealed record TableIndexList(IReadOnlyList<TableIndexSize> Indexes, bool IsLocked, DateTime? ServerStarted, IReadOnlyList<TableReadFailure> Failures);

/// <summary>What Table Statistics works out from what it read (#242).</summary>
public static class TableSpace
{
    private const long KbPerPage = 8;

    /// <summary>What a value that isn't known shows.</summary>
    public const string Unknown = "–";

    /// <summary>From here an index counts as large: 100 MB.</summary>
    public const long LargeIndexPages = 100 * 1024 / KbPerPage;

    /// <summary>From here an index counts as much written.</summary>
    public const long ManyWrites = 1_000;

    /// <summary>An index counts as little read at one read or fewer per this many writes.</summary>
    public const long WritesPerRead = 10;

    /// <summary>"512 KB", "629 MB", "1.7 GB"; a dash when not known.</summary>
    public static string SizeText(long? pages)
    {
        if (pages is not { } known)
            return Unknown;
        var culture = CultureInfo.CurrentCulture;
        double kb = known * KbPerPage, mb = kb / 1024, gb = mb / 1024, tb = gb / 1024;
        return tb >= 1 ? string.Format(culture, "{0:0.#} TB", tb)
             : gb >= 1 ? string.Format(culture, "{0:0.#} GB", gb)
             : mb >= 1 ? string.Format(culture, "{0:N0} MB", mb)
             : string.Format(culture, "{0:N0} KB", kb);
    }

    /// <summary>"Nonclustered", "Clustered columnstore": a type as the grid shows it.</summary>
    public static string TypeText(string type) =>
        type.Length == 0 ? type : char.ToUpperInvariant(type[0]) + type[1..].ToLowerInvariant();

    /// <summary>
    /// A large index read little and written much, so it may not be worth keeping. Never the table's
    /// data, an index enforcing a rule, or one whose counts couldn't be read.
    /// </summary>
    public static bool IsCostly(TableIndexSize index) =>
        !index.IsData && !index.IsUnique
        && index.ReservedPages >= LargeIndexPages
        && index.Writes >= ManyWrites
        && index.Reads * WritesPerRead <= index.Writes;

    /// <summary>
    /// The tables by name, with their sizes, the largest first and the locked ones last. A table with
    /// no row in sizes that were read holds nothing (an external table); <paramref name="sizes"/> null
    /// means they couldn't be read. A locked table's row can be mid-change, so it isn't used.
    /// </summary>
    public static IReadOnlyList<TableSize> Merge(IEnumerable<(int ObjectId, string Schema, string Table)> tables,
                                                 IReadOnlyDictionary<int, (long Rows, long DataPages, long IndexPages)>? sizes,
                                                 IReadOnlySet<int> locked) =>
        tables.Select(t =>
              {
                  if (locked.Contains(t.ObjectId))
                      return new TableSize(t.ObjectId, t.Schema, t.Table, null, null, null, true);
                  if (sizes is null)
                      return new TableSize(t.ObjectId, t.Schema, t.Table, null, null, null, false);
                  var size = sizes.GetValueOrDefault(t.ObjectId);
                  return new TableSize(t.ObjectId, t.Schema, t.Table, size.Rows, size.DataPages, size.IndexPages, false);
              })
              .OrderByDescending(t => t.TotalPages ?? -1)
              .ThenBy(t => t.SchemaName, StringComparer.OrdinalIgnoreCase)
              .ThenBy(t => t.TableName, StringComparer.OrdinalIgnoreCase)
              .ToList();
}
