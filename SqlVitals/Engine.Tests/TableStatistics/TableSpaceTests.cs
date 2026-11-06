using System.Globalization;
using SqlVitals.Engine.TableStatistics;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Engine.Tests.TableStatistics;

/// <summary>What Table Statistics works out (#242): sizes in words, the flag, the order and the verdict.</summary>
public class TableSpaceTests
{
    private const long Mb = 128;

    private static TableIndexSize Index(long pages = 200 * Mb, long? reads = 17, long? writes = 972_000, int id = 2, bool unique = false) =>
        new(id, "IX", "NONCLUSTERED", unique, 10, pages, pages, reads, writes);

    private static TableSize Table(string name, long? data, long? index, bool locked = false, string schema = "dbo") =>
        new(name.GetHashCode(StringComparison.Ordinal), schema, name, data is null ? null : 10, data, index, locked);

    [Theory]
    [InlineData(1L, "8 KB")]
    [InlineData(127L, "1,016 KB")]
    [InlineData(128L, "1 MB")]
    [InlineData(80_512L, "629 MB")]
    [InlineData(131_072L, "1 GB")]
    [InlineData(222_822L, "1.7 GB")]
    [InlineData(134_217_728L, "1 TB")]
    public void Sizes_ReadInTheUnitThatFits(long pages, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, TableSpace.SizeText(pages));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void ASizeNotKnown_IsADash() => Assert.Equal(TableSpace.Unknown, TableSpace.SizeText(null));

    [Theory]
    [InlineData("NONCLUSTERED", "Nonclustered")]
    [InlineData("CLUSTERED COLUMNSTORE", "Clustered columnstore")]
    [InlineData("HEAP", "Heap")]
    public void Types_ReadAsWords(string type, string expected) => Assert.Equal(expected, TableSpace.TypeText(type));

    [Fact]
    public void ALargeIndex_ReadLittleAndWrittenMuch_IsFlagged() => Assert.True(TableSpace.IsCostly(Index()));

    [Fact]
    public void Flag_HoldsAtEachLimit()
    {
        Assert.True(TableSpace.IsCostly(Index(pages: TableSpace.LargeIndexPages, reads: 100, writes: TableSpace.ManyWrites)));
        Assert.False(TableSpace.IsCostly(Index(pages: TableSpace.LargeIndexPages - 1, reads: 100, writes: TableSpace.ManyWrites)));
        Assert.False(TableSpace.IsCostly(Index(pages: TableSpace.LargeIndexPages, reads: 101, writes: TableSpace.ManyWrites)));
        Assert.False(TableSpace.IsCostly(Index(pages: TableSpace.LargeIndexPages, reads: 0, writes: TableSpace.ManyWrites - 1)));
    }

    [Fact]
    public void Flag_SparesTheTablesData_AUniqueIndex_AndUnknownCounts()
    {
        Assert.False(TableSpace.IsCostly(Index(id: 1)));
        Assert.False(TableSpace.IsCostly(Index(id: 0)));
        Assert.False(TableSpace.IsCostly(Index(unique: true)));
        Assert.False(TableSpace.IsCostly(Index(reads: null)));
        Assert.False(TableSpace.IsCostly(Index(writes: null)));
    }

    [Fact]
    public void Merge_IsLargestFirst_WithLockedLast_AndIgnoresALockedTablesRow()
    {
        var sizes = new Dictionary<int, (long Rows, long DataPages, long IndexPages)>
        {
            [1] = (5, 10, 0),
            [2] = (5, 100, 50),
            [3] = (9, 9999, 9999),
        };
        var merged = TableSpace.Merge([(1, "dbo", "Small"), (2, "dbo", "Big"), (3, "dbo", "Held"), (4, "dbo", "External")],
                                      sizes, new HashSet<int> { 3 });

        Assert.Equal(["Big", "Small", "External", "Held"], merged.Select(t => t.TableName));
        Assert.Equal(150, merged[0].TotalPages);
        Assert.Equal(0, merged[2].TotalPages);
        Assert.True(merged[3].IsLocked);
        Assert.Null(merged[3].TotalPages);
        Assert.Null(merged[3].RowsInTable);
    }

    [Fact]
    public void Merge_WithoutSizes_ListsTheNames()
    {
        var merged = TableSpace.Merge([(2, "dbo", "B"), (1, "dbo", "A")], null, new HashSet<int>());

        Assert.Equal(["A", "B"], merged.Select(t => t.TableName));
        Assert.All(merged, t => Assert.Null(t.TotalPages));
        Assert.All(merged, t => Assert.False(t.IsLocked));
    }

    // ── The verdict ─────────────────────────────────────────────────────────

    [Fact]
    public void Verdict_NamesTheLargest_AndItsIndexesShare()
    {
        var verdict = TableStatisticsVerdict.From("SalesDb", [Table("Orders", 300 * Mb, 100 * Mb), Table("Small", Mb, 0)], []);

        Assert.Equal(VerdictLevel.Info, verdict.Level);
        Assert.Contains("dbo.Orders is the largest of 2 tables in SalesDb: 400 MB", verdict.Headline);
        Assert.Contains("100 MB", verdict.Why);
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, "{0:0.#} %", 25.0), verdict.Why);
        Assert.Equal(TableStatisticsVerdict.ShowLargest, Assert.Single(verdict.Actions).Key);
    }

    [Fact]
    public void Verdict_OnOneEmptyTable_DoesNotDivideByZero()
    {
        var verdict = TableStatisticsVerdict.From("SalesDb", [Table("Empty", 0, 0)], []);

        Assert.Contains("dbo.Empty is the only table in SalesDb", verdict.Headline);
    }

    [Fact]
    public void Verdict_NoTables_IsFine() =>
        Assert.Equal(VerdictLevel.Ok, TableStatisticsVerdict.From("SalesDb", [], []).Level);

    [Fact]
    public void Verdict_SaysWhichTablesAreLocked_AndStillNamesTheLargest()
    {
        var verdict = TableStatisticsVerdict.From("SalesDb", [Table("Orders", Mb, 0), Table("Held", null, null, locked: true)], []);

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("dbo.Orders is the largest table read in SalesDb: 1 MB", verdict.Headline);
        Assert.Contains("1 table is under a schema change", verdict.Why);
        Assert.Contains("left out of these figures", verdict.Why);
    }

    [Fact]
    public void Verdict_WhenEveryTableIsLocked_SaysThat()
    {
        var verdict = TableStatisticsVerdict.From("SalesDb", [Table("Held", null, null, locked: true)], []);

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Contains("1 table is in SalesDb under a schema change", verdict.Headline);
        Assert.Empty(verdict.Actions);
    }

    [Fact]
    public void Verdict_NamesTheMissingPermission_AndItsGrant()
    {
        var failure = new TableReadFailure(TableReadFailure.Sizes, "The login lacks VIEW DATABASE STATE in SalesDb.", "USE [SalesDb];\nGRANT VIEW DATABASE STATE TO [app];");

        var verdict = TableStatisticsVerdict.From("SalesDb", [Table("Orders", null, null)], [failure]);

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("Couldn't read the table sizes in SalesDb", verdict.Headline);
        Assert.Contains("VIEW DATABASE STATE", verdict.Why);
        Assert.Contains("GRANT VIEW DATABASE STATE TO [app];", verdict.Why);
    }

    [Fact]
    public void Verdict_WhenTheLoginMayNotSeeEveryTable_DoesntClaimToHaveAllOfThem()
    {
        var hidden = new TableReadFailure(TableReadFailure.Names, "The login lacks VIEW DEFINITION in SalesDb.", null);

        var some = TableStatisticsVerdict.From("SalesDb", [Table("Orders", Mb, 0), Table("Small", 1, 0)], [hidden]);
        Assert.Equal("dbo.Orders is the largest table read in SalesDb: 1 MB", some.Headline);
        Assert.Contains("VIEW DEFINITION", some.Why);

        var none = TableStatisticsVerdict.From("SalesDb", [], [hidden]);
        Assert.Equal(VerdictLevel.Warning, none.Level);
        Assert.Equal("This login can't see any tables in SalesDb", none.Headline);
        Assert.Contains("VIEW DEFINITION", none.Why);
    }

    [Fact]
    public void Verdict_OnALesserFailure_KeepsTheSizes_AndSaysWhatIsMissing()
    {
        var failure = new TableReadFailure("which tables are locked", "The login lacks VIEW SERVER STATE.", null);

        var verdict = TableStatisticsVerdict.From("SalesDb", [Table("Orders", Mb, 0)], [failure]);

        Assert.Contains("dbo.Orders", verdict.Headline);
        Assert.Contains("Not read: which tables are locked. The login lacks VIEW SERVER STATE.", verdict.Why);
    }
}
