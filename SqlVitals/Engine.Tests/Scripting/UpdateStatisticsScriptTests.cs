using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Tests.Scripting;

public class UpdateStatisticsScriptTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 10, 0, 0);

    private static StaleStatistic Stat(string name = "IX_Orders_CustomerId", long rows = 250_000,
                                       double samplePct = 12.5, long modifications = 40_000,
                                       bool incremental = false, DateTime? lastUpdated = null,
                                       string db = "Sales", string schema = "dbo", string table = "Orders") =>
        new(schema, table, name, lastUpdated ?? new DateTime(2026, 8, 1), 53, rows,
            (long)(rows * samplePct / 100), samplePct, modifications, incremental,
            AutoCreated: false, UserCreated: false, db, Now);

    // ── Sampling ──────────────────────────────────────────────────────

    [Fact]
    public void Build_WithFullScan_AddsTheOption()
    {
        var script = UpdateStatisticsScript.Build([Stat()], StatisticsSampling.FullScan, Now);

        Assert.Contains("UPDATE STATISTICS [dbo].[Orders] ([IX_Orders_CustomerId]) WITH FULLSCAN;", script);
        Assert.Contains("Sampling:   FULLSCAN", script);
    }

    [Fact]
    public void Build_WithDefaultSampling_HasNoWithClause()
    {
        var script = UpdateStatisticsScript.Build([Stat()], StatisticsSampling.Default, Now);

        Assert.Contains("UPDATE STATISTICS [dbo].[Orders] ([IX_Orders_CustomerId]);", script);
        Assert.DoesNotContain("FULLSCAN;", script);
        Assert.DoesNotContain(" WITH ", script);
        Assert.Contains("Sampling:   default", script);
    }

    [Fact]
    public void Build_WithFullScan_WarnsAboutTheRowsRead()
    {
        var script = UpdateStatisticsScript.Build(
            [Stat(rows: 1_000_000), Stat(name: "_WA_Sys_00000002", rows: 1_000_000)], StatisticsSampling.FullScan, Now);

        Assert.Contains("about 2,000,000 rows in total", script);
    }

    [Fact]
    public void Build_WithDefaultSampling_FlagsStatisticsLastBuiltWithAFullScan()
    {
        var script = UpdateStatisticsScript.Build(
            [Stat(samplePct: 100), Stat(name: "_WA_Sys_00000002", samplePct: 20)], StatisticsSampling.Default, Now);

        Assert.Equal(1, CountOf(script, "-- NOTE: last built with a full scan"));
    }

    [Fact]
    public void Build_WithFullScan_DoesNotFlagStatisticsLastBuiltWithAFullScan()
    {
        var script = UpdateStatisticsScript.Build([Stat(samplePct: 100)], StatisticsSampling.FullScan, Now);

        Assert.DoesNotContain("-- NOTE: last built with a full scan", script);
    }

    // ── Script shape ──────────────────────────────────────────────────

    [Fact]
    public void Build_StartsWithReviewWarning()
    {
        var script = UpdateStatisticsScript.Build([Stat()], StatisticsSampling.Default, Now);

        Assert.StartsWith("/*", script);
        Assert.Contains("REVIEW BEFORE RUNNING:", script);
        Assert.True(script.IndexOf("REVIEW BEFORE RUNNING:", StringComparison.Ordinal)
                    < script.IndexOf("UPDATE STATISTICS [", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_GuardsEachStatementAgainstADroppedStatistic()
    {
        var script = UpdateStatisticsScript.Build([Stat()], StatisticsSampling.Default, Now);

        Assert.Contains(
            "IF EXISTS (SELECT 1 FROM sys.stats WHERE object_id = OBJECT_ID(N'[dbo].[Orders]') AND name = N'IX_Orders_CustomerId')",
            script);
    }

    [Fact]
    public void Build_GroupsStatisticsByDatabase()
    {
        var script = UpdateStatisticsScript.Build(
            [Stat(db: "A"), Stat(db: "B", table: "Lines"), Stat(db: "A", table: "Lines")], StatisticsSampling.Default, Now);

        Assert.Equal(1, CountOf(script, "USE [A];"));
        Assert.Equal(1, CountOf(script, "USE [B];"));
        Assert.Equal(3, CountOf(script, "UPDATE STATISTICS ["));
    }

    [Fact]
    public void Build_KeepsTheOrderItWasGiven()
    {
        var script = UpdateStatisticsScript.Build(
            [Stat(table: "Zeta"), Stat(table: "Alpha")], StatisticsSampling.Default, Now);

        Assert.True(script.IndexOf("[dbo].[Zeta] (", StringComparison.Ordinal)
                    < script.IndexOf("[dbo].[Alpha] (", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_QualifiesTheTableWithItsSchema()
    {
        var script = UpdateStatisticsScript.Build([Stat(schema: "Sales")], StatisticsSampling.Default, Now);

        Assert.Contains("UPDATE STATISTICS [Sales].[Orders] ([IX_Orders_CustomerId]);", script);
    }

    [Fact]
    public void Build_EscapesBracketsAndQuotesInNames()
    {
        var script = UpdateStatisticsScript.Build(
            [Stat(name: "ST_O]rd'ers", table: "Or]d'ers")], StatisticsSampling.Default, Now);

        Assert.Contains("UPDATE STATISTICS [dbo].[Or]]d'ers] ([ST_O]]rd'ers]);", script);
        // The table goes into OBJECT_ID() as a quoted name, so its brackets double; sys.stats.name
        // holds the statistic's name as it really is, so that literal keeps the single bracket.
        Assert.Contains("OBJECT_ID(N'[dbo].[Or]]d''ers]') AND name = N'ST_O]rd''ers'", script);
    }

    [Fact]
    public void Build_NotesIncrementalStatistics()
    {
        var script = UpdateStatisticsScript.Build([Stat(incremental: true)], StatisticsSampling.FullScan, Now);

        Assert.Contains("-- NOTE: incremental statistic", script);
        Assert.Contains("WITH FULLSCAN;", script);
    }

    [Fact]
    public void Build_ShowsTheStatisticDetailsAboveEachStatement()
    {
        var script = UpdateStatisticsScript.Build([Stat()], StatisticsSampling.Default, Now);

        Assert.Contains("-- Modifications: 40,000  Rows: 250,000  Last updated: 2026-08-01 (53 days)  Last sample: 12.5%", script);
    }

    [Fact]
    public void Build_CollapsesDuplicateRows()
    {
        var script = UpdateStatisticsScript.Build([Stat(), Stat()], StatisticsSampling.Default, Now);

        Assert.Equal(1, CountOf(script, "UPDATE STATISTICS [dbo].[Orders]"));
        Assert.Contains("Statistics: 1 on 1 table(s)", script);
    }

    [Fact]
    public void Build_WithNoStatistics_SaysSo()
    {
        var script = UpdateStatisticsScript.Build([], StatisticsSampling.Default, Now);

        Assert.Contains("-- No statistics to update.", script);
        Assert.DoesNotContain("UPDATE STATISTICS [", script);
    }

    // ── Summary ───────────────────────────────────────────────────────

    [Fact]
    public void Summarize_CountsStatisticsTablesAndRows()
    {
        var summary = UpdateStatisticsScript.Summarize(
            [Stat(rows: 100), Stat(name: "_WA_Sys_00000002", rows: 100), Stat(table: "Lines", rows: 50),
             Stat(db: "Other", rows: 10)]);

        Assert.Equal(4, summary.Statistics);
        Assert.Equal(3, summary.Tables);   // Sales.dbo.Orders, Sales.dbo.Lines, Other.dbo.Orders
        Assert.Equal(260, summary.RowsInTables);
    }

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, "").Length) / value.Length;
}
