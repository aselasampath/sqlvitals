using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Tests.Scripting;

public class UnusedIndexDropScriptTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 10, 0, 0);

    private static UnusedIndex Index(string name, string db = "Sales", string schema = "dbo",
                                     string table = "Orders", bool isUnique = false) =>
        new(db, schema, table, name, "NONCLUSTERED", isUnique, false, false,
            0, 0, 0, 1234, null, null, null, 0, 5000, Now);

    [Fact]
    public void Build_EmitsGuardedDropForEachIndex()
    {
        var script = UnusedIndexDropScript.Build([Index("IX_Orders_Date")], Now);

        Assert.Contains("USE [Sales];", script);
        Assert.Contains("IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Orders]') AND name = N'IX_Orders_Date')", script);
        Assert.Contains("    DROP INDEX [IX_Orders_Date] ON [dbo].[Orders];", script);
    }

    [Fact]
    public void Build_CommentsOutUniqueIndexes()
    {
        var script = UnusedIndexDropScript.Build([Index("UX_Orders_Ref", isUnique: true)], Now);

        Assert.Contains("-- SKIPPED: unique index", script);
        Assert.Contains("--     DROP INDEX [UX_Orders_Ref] ON [dbo].[Orders];", script);
        Assert.DoesNotContain("\n    DROP INDEX", script);
    }

    [Fact]
    public void Build_EscapesBracketsAndQuotesInNames()
    {
        var script = UnusedIndexDropScript.Build([Index("IX_a]b'c", table: "Or]ders")], Now);

        Assert.Contains("DROP INDEX [IX_a]]b'c] ON [dbo].[Or]]ders];", script);
        Assert.Contains("OBJECT_ID(N'[dbo].[Or]]ders]') AND name = N'IX_a]b''c'", script);
    }

    [Fact]
    public void Build_GroupsIndexesByDatabase()
    {
        var script = UnusedIndexDropScript.Build(
            [Index("IX_1", db: "A"), Index("IX_2", db: "B"), Index("IX_3", db: "A")], Now);

        Assert.Equal(1, CountOf(script, "USE [A];"));
        Assert.Equal(1, CountOf(script, "USE [B];"));
    }

    [Fact]
    public void Build_WithNoIndexes_SaysSo()
    {
        var script = UnusedIndexDropScript.Build([], Now);

        Assert.Contains("-- No unused indexes to drop.", script);
        Assert.DoesNotContain("DROP INDEX [", script);
    }

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, "").Length) / value.Length;
}
