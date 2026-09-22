using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Tests.Scripting;

public class MissingIndexCreateScriptTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 10, 0, 0);

    private static MissingIndex Suggestion(string? equality = "[CustomerId]", string? inequality = null,
                                           string? included = null, string db = "Sales",
                                           string schema = "dbo", string table = "Orders") =>
        new(db, schema, table, equality, inequality, included,
            10, 500, 20, Now, null, 12.5, 90, 5625, "CRITICAL", "", Now);

    [Fact]
    public void Build_EmitsGuardedCreateWithKeyAndIncludeColumns()
    {
        var script = MissingIndexCreateScript.Build(
            [Suggestion("[CustomerId], [Status]", "[OrderDate]", "[Total], [ShipDate]")], Now);

        Assert.Contains("USE [Sales];", script);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[Orders]') IS NOT NULL", script);
        Assert.Contains("AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[Orders]') AND name = N'IX_Orders_CustomerId_Status_OrderDate')", script);
        Assert.Contains("    CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId_Status_OrderDate] ON [dbo].[Orders] ([CustomerId], [Status], [OrderDate])", script);
        Assert.Contains("    INCLUDE ([Total], [ShipDate]);", script);
    }

    [Fact]
    public void Build_WithoutIncludeColumns_EndsCreateOnKeyLine()
    {
        var script = MissingIndexCreateScript.Build([Suggestion(included: null)], Now);

        Assert.Contains("ON [dbo].[Orders] ([CustomerId]);", script);
        Assert.DoesNotContain("INCLUDE (", script);
    }

    [Fact]
    public void Build_UsesInequalityColumnsWhenThereAreNoEqualityColumns()
    {
        var script = MissingIndexCreateScript.Build([Suggestion(equality: null, inequality: "[OrderDate]")], Now);

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Orders_OrderDate] ON [dbo].[Orders] ([OrderDate]);", script);
    }

    [Fact]
    public void Build_StartsWithReviewWarning()
    {
        var script = MissingIndexCreateScript.Build([Suggestion()], Now);

        Assert.StartsWith("/*", script);
        Assert.Contains("REVIEW BEFORE RUNNING:", script);
        Assert.True(script.IndexOf("REVIEW BEFORE RUNNING:", StringComparison.Ordinal)
                    < script.IndexOf("CREATE NONCLUSTERED INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_EscapesBracketsAndQuotesInNames()
    {
        var script = MissingIndexCreateScript.Build(
            [Suggestion("[Cust]]Id]", table: "Or]d'ers")], Now);

        Assert.Contains("ON [dbo].[Or]]d'ers] ([Cust]]Id]);", script);
        Assert.Contains("OBJECT_ID(N'[dbo].[Or]]d''ers]')", script);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Or_d_ers_Cust_Id]", script);
    }

    [Fact]
    public void Build_GivesSuggestionsWithTheSameKeysDistinctNames()
    {
        var script = MissingIndexCreateScript.Build(
            [Suggestion(included: "[Total]"), Suggestion(included: "[ShipDate]")], Now);

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId] ON", script);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId_2] ON", script);
    }

    [Fact]
    public void Build_GroupsSuggestionsByDatabase()
    {
        var script = MissingIndexCreateScript.Build(
            [Suggestion(db: "A"), Suggestion(db: "B", table: "Lines"), Suggestion(db: "A", table: "Lines")], Now);

        Assert.Equal(1, CountOf(script, "USE [A];"));
        Assert.Equal(1, CountOf(script, "USE [B];"));
    }

    [Fact]
    public void Build_SkipsSuggestionWithNoKeyColumns()
    {
        var script = MissingIndexCreateScript.Build([Suggestion(equality: null, inequality: null)], Now);

        Assert.Contains("-- SKIPPED:", script);
        Assert.DoesNotContain("CREATE NONCLUSTERED INDEX [", script);
    }

    [Fact]
    public void Build_WithNoSuggestions_SaysSo()
    {
        var script = MissingIndexCreateScript.Build([], Now);

        Assert.Contains("-- No missing-index suggestions to create.", script);
        Assert.DoesNotContain("CREATE NONCLUSTERED INDEX [", script);
    }

    [Fact]
    public void IndexName_IsCutToFitSysname()
    {
        var columns = string.Join(", ", Enumerable.Range(1, 20).Select(i => $"[SomeFairlyLongColumnName{i}]"));

        var name = MissingIndexCreateScript.IndexName(Suggestion(columns));

        Assert.Equal(128, name.Length);
        Assert.StartsWith("IX_Orders_SomeFairlyLongColumnName1_", name);
    }

    [Theory]
    [InlineData("[A], [B]", new[] { "A", "B" })]
    [InlineData("[Order]]Date], [Has, Comma]", new[] { "Order]Date", "Has, Comma" })]
    [InlineData("", new string[0])]
    public void ParseColumns_SplitsBracketedList(string list, string[] expected)
    {
        Assert.Equal(expected, MissingIndexCreateScript.ParseColumns(list));
    }

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, "").Length) / value.Length;
}
