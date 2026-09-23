using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Tests.Scripting;

public class IndexMaintenanceScriptTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 10, 0, 0);

    private static IndexFragmentation Index(double fragPct = 45, long pages = 5000,
                                            string? indexName = "IX_Orders_CustomerId",
                                            string indexType = "NONCLUSTERED INDEX",
                                            string db = "Sales", string schema = "dbo", string table = "Orders") =>
        new(schema, table, indexName, indexType, fragPct, pages, 72.5, 250_000, db,
            fragPct < 10 ? "No Action Needed" : fragPct < 30 ? "Reorganize" : "Rebuild", Now);

    private static IndexMaintenanceOptions Options(double reorg = 5, double rebuild = 30,
                                                   long minPages = 1000, bool online = false) =>
        new()
        {
            ReorganizeThresholdPercent = reorg,
            RebuildThresholdPercent    = rebuild,
            MinPageCount               = minPages,
            OnlineRebuild              = online,
        };

    // ── Thresholds ────────────────────────────────────────────────────

    [Theory]
    [InlineData(5.0)]
    [InlineData(17.4)]
    [InlineData(29.9)]
    public void Build_BetweenThresholds_Reorganizes(double fragPct)
    {
        var script = IndexMaintenanceScript.Build([Index(fragPct)], Options(), Now);

        Assert.Contains("ALTER INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] REORGANIZE;", script);
        Assert.DoesNotContain("REBUILD;", script);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(91.2)]
    public void Build_AtOrAboveRebuildThreshold_Rebuilds(double fragPct)
    {
        var script = IndexMaintenanceScript.Build([Index(fragPct)], Options(), Now);

        Assert.Contains("ALTER INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] REBUILD;", script);
        Assert.DoesNotContain("REORGANIZE;", script);
    }

    [Fact]
    public void Build_BelowReorganizeThreshold_SkipsWithReason()
    {
        var script = IndexMaintenanceScript.Build([Index(4.9)], Options(), Now);

        Assert.Contains("-- SKIPPED: 4.9% is below the 5% reorganize threshold.", script);
        Assert.DoesNotContain("ALTER INDEX", script);
    }

    [Fact]
    public void Build_HonoursCustomThresholds()
    {
        var script = IndexMaintenanceScript.Build(
            [Index(20), Index(60, table: "Lines")], Options(reorg: 15, rebuild: 50), Now);

        Assert.Contains("ON [dbo].[Orders] REORGANIZE;", script);
        Assert.Contains("ON [dbo].[Lines] REBUILD;", script);
        Assert.Contains("- REORGANIZE from 15% fragmentation", script);
        Assert.Contains("- REBUILD from 50%", script);
    }

    [Fact]
    public void Build_WithThresholdsMeeting_PrefersRebuild()
    {
        // Reorganize and rebuild set to the same number: the heavier fix wins at that value.
        var script = IndexMaintenanceScript.Build([Index(30)], Options(reorg: 30, rebuild: 30), Now);

        Assert.Contains("REBUILD;", script);
        Assert.DoesNotContain("REORGANIZE;", script);
    }

    // ── Minimum page count ────────────────────────────────────────────

    [Fact]
    public void Build_BelowMinPageCount_SkipsEvenWhenBadlyFragmented()
    {
        var script = IndexMaintenanceScript.Build([Index(99, pages: 999)], Options(minPages: 1000), Now);

        Assert.Contains("-- SKIPPED: 999 pages is below the 1,000 page minimum", script);
        Assert.DoesNotContain("ALTER INDEX", script);
    }

    [Fact]
    public void Build_AtMinPageCount_IsMaintained()
    {
        var script = IndexMaintenanceScript.Build([Index(99, pages: 1000)], Options(minPages: 1000), Now);

        Assert.Contains("ALTER INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] REBUILD;", script);
    }

    // ── ONLINE = ON ───────────────────────────────────────────────────

    [Fact]
    public void Build_WithOnlineRebuild_AddsTheOption()
    {
        var script = IndexMaintenanceScript.Build([Index(45)], Options(online: true), Now);

        Assert.Contains("ALTER INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] REBUILD WITH (ONLINE = ON);", script);
        Assert.Contains("- ONLINE = ON: yes, on rowstore rebuilds", script);
    }

    [Fact]
    public void Build_WithoutOnlineRebuild_LeavesTheOptionOut()
    {
        var script = IndexMaintenanceScript.Build([Index(45)], Options(online: false), Now);

        Assert.DoesNotContain("ONLINE = ON)", script);
        Assert.Contains("- ONLINE = ON: no", script);
    }

    [Fact]
    public void Build_OnlineRebuild_IsNotAppliedToReorganize()
    {
        var script = IndexMaintenanceScript.Build([Index(20)], Options(online: true), Now);

        Assert.Contains("REORGANIZE;", script);
        Assert.DoesNotContain("ONLINE = ON)", script);
    }

    [Theory]
    [InlineData("CLUSTERED COLUMNSTORE INDEX")]
    [InlineData("PRIMARY XML INDEX")]
    [InlineData("SPATIAL INDEX")]
    public void Build_OnlineRebuild_IsNotAppliedToIndexTypesThatRejectIt(string indexType)
    {
        var script = IndexMaintenanceScript.Build([Index(45, indexType: indexType)], Options(online: true), Now);

        Assert.Contains($"-- NOTE: ONLINE = ON is not available for a {indexType}", script);
        Assert.Contains("ON [dbo].[Orders] REBUILD;", script);
    }

    [Theory]
    [InlineData(3, true)]   // Enterprise / Developer / Evaluation
    [InlineData(5, true)]   // Azure SQL Database
    [InlineData(8, true)]   // Azure SQL Managed Instance
    [InlineData(1, false)]  // Personal / Desktop
    [InlineData(2, false)]  // Standard / Web / BI
    [InlineData(4, false)]  // Express
    [InlineData(0, false)]  // edition could not be read
    public void EditionSupportsOnlineRebuild_MatchesTheEditionsThatAllowIt(int engineEdition, bool expected)
    {
        Assert.Equal(expected, IndexMaintenanceScript.EditionSupportsOnlineRebuild(engineEdition));
    }

    // ── Script shape ──────────────────────────────────────────────────

    [Fact]
    public void Build_StartsWithReviewWarning()
    {
        var script = IndexMaintenanceScript.Build([Index()], Options(), Now);

        Assert.StartsWith("/*", script);
        Assert.Contains("REVIEW BEFORE RUNNING:", script);
        Assert.True(script.IndexOf("REVIEW BEFORE RUNNING:", StringComparison.Ordinal)
                    < script.IndexOf("ALTER INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_GuardsEachStatementAgainstADroppedIndex()
    {
        var script = IndexMaintenanceScript.Build([Index()], Options(), Now);

        Assert.Contains(
            "IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Orders]') AND name = N'IX_Orders_CustomerId')",
            script);
    }

    [Fact]
    public void Build_GroupsIndexesByDatabase()
    {
        var script = IndexMaintenanceScript.Build(
            [Index(db: "A"), Index(db: "B", table: "Lines"), Index(db: "A", table: "Lines")], Options(), Now);

        Assert.Equal(1, CountOf(script, "USE [A];"));
        Assert.Equal(1, CountOf(script, "USE [B];"));
    }

    [Fact]
    public void Build_QualifiesTheTableWithItsSchema()
    {
        var script = IndexMaintenanceScript.Build([Index(schema: "Sales")], Options(), Now);

        Assert.Contains("ON [Sales].[Orders] REBUILD;", script);
    }

    [Fact]
    public void Build_EscapesBracketsAndQuotesInNames()
    {
        var script = IndexMaintenanceScript.Build(
            [Index(indexName: "IX_O]rders", table: "Or]d'ers")], Options(), Now);

        Assert.Contains("ALTER INDEX [IX_O]]rders] ON [dbo].[Or]]d'ers] REBUILD;", script);
        // The table goes into OBJECT_ID() as a quoted name, so its brackets double; sys.indexes.name
        // holds the index name as it really is, so that literal keeps the single bracket.
        Assert.Contains("OBJECT_ID(N'[dbo].[Or]]d''ers]') AND name = N'IX_O]rders'", script);
    }

    [Fact]
    public void Build_CollapsesThePerPartitionRowsOfOneIndex()
    {
        // dm_db_index_physical_stats returns a row per partition; ALTER INDEX covers them all.
        var script = IndexMaintenanceScript.Build([Index(12), Index(48), Index(31)], Options(), Now);

        Assert.Equal(1, CountOf(script, "ALTER INDEX [IX_Orders_CustomerId]"));
        // The worst partition decides the action.
        Assert.Contains("REBUILD;", script);
        Assert.Contains("-- Fragmentation: 48.0%", script);
    }

    [Fact]
    public void Build_SkipsHeapsAndSaysWhy()
    {
        var script = IndexMaintenanceScript.Build([Index(indexName: null, indexType: "HEAP")], Options(), Now);

        Assert.Contains("-- SKIPPED: a heap has no index to alter.", script);
        Assert.Contains("-- ALTER TABLE [dbo].[Orders] REBUILD;", script);
        Assert.DoesNotContain("\nALTER TABLE", script);
        Assert.DoesNotContain("ALTER INDEX", script);
    }

    [Fact]
    public void Build_WithNoIndexes_SaysSo()
    {
        var script = IndexMaintenanceScript.Build([], Options(), Now);

        Assert.Contains("-- No fragmented indexes to maintain.", script);
        Assert.DoesNotContain("ALTER INDEX", script);
    }

    [Fact]
    public void Build_HeaderCountsEachOutcome()
    {
        var script = IndexMaintenanceScript.Build(
            [Index(45), Index(20, table: "Lines"), Index(2, table: "Items")], Options(), Now);

        Assert.Contains("Indexes:   3 — 1 to rebuild, 1 to reorganize, 1 skipped", script);
    }

    // ── Summary ───────────────────────────────────────────────────────

    [Fact]
    public void Summarize_CountsTheSameOutcomesAsTheScript()
    {
        var summary = IndexMaintenanceScript.Summarize(
            [Index(45), Index(60, table: "Lines"), Index(20, table: "Items"),
             Index(2, table: "Prices"), Index(99, pages: 10, table: "Tiny")], Options());

        Assert.Equal(2, summary.Rebuild);
        Assert.Equal(1, summary.Reorganize);
        Assert.Equal(2, summary.Skipped);
    }

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, "").Length) / value.Length;
}
