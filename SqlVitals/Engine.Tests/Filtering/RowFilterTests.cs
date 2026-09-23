using System.Globalization;
using SqlVitals.Engine.Filtering;

namespace SqlVitals.Engine.Tests.Filtering;

public class RowFilterTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    private static bool Matches(string filter, params string[] cells) =>
        RowFilter.Matches(RowFilter.RowText(cells), RowFilter.ParseTerms(filter));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void ParseTerms_BlankFilter_HasNoTerms(string? filter)
    {
        Assert.Empty(RowFilter.ParseTerms(filter));
    }

    [Fact]
    public void ParseTerms_SplitsOnWhitespace()
    {
        Assert.Equal(["Orders", "IX_Orders"], RowFilter.ParseTerms("  Orders \t IX_Orders "));
    }

    [Fact]
    public void ParseTerms_KeepsQuotedPhraseTogether()
    {
        Assert.Equal(["order by", "Sales"], RowFilter.ParseTerms("\"order by\" Sales"));
    }

    [Fact]
    public void ParseTerms_UnclosedQuote_RunsToTheEnd()
    {
        Assert.Equal(["dbo", "select top"], RowFilter.ParseTerms("dbo \"select top"));
    }

    [Fact]
    public void ParseTerms_DropsDuplicatesIgnoringCase()
    {
        Assert.Equal(["Orders"], RowFilter.ParseTerms("Orders orders ORDERS"));
    }

    [Fact]
    public void Matches_NoTerms_MatchesEveryRow()
    {
        Assert.True(Matches("", "anything"));
    }

    [Fact]
    public void Matches_IgnoresCase()
    {
        Assert.True(Matches("ix_orders", "Sales", "IX_Orders_CustomerId"));
    }

    [Fact]
    public void Matches_RequiresEveryTerm_InAnyColumn()
    {
        Assert.True(Matches("sales customer", "Sales", "dbo", "IX_Orders_CustomerId"));
        Assert.False(Matches("sales invoice", "Sales", "dbo", "IX_Orders_CustomerId"));
    }

    [Fact]
    public void Matches_PhraseMustAppearAsWritten()
    {
        Assert.True(Matches("\"order by\"", "SELECT * FROM t ORDER BY id"));
        Assert.False(Matches("\"order by\"", "SELECT order_id FROM t GROUP BY id"));
    }

    [Fact]
    public void Matches_NeverSpansTwoCells()
    {
        Assert.False(Matches("SalesOrders", "Sales", "Orders"));
    }

    [Fact]
    public void CellText_Null_IsEmpty()
    {
        Assert.Equal("", RowFilter.CellText(null, "N0", EnUs));
    }

    [Fact]
    public void CellText_String_IsItself()
    {
        Assert.Equal("Rebuild", RowFilter.CellText("Rebuild", null, EnUs));
    }

    [Fact]
    public void CellText_FormattedNumber_FindsShownAndRawValue()
    {
        var row = RowFilter.RowText([RowFilter.CellText(40000L, "N0", EnUs)]);

        Assert.True(RowFilter.Matches(row, ["40,000"]));
        Assert.True(RowFilter.Matches(row, ["40000"]));
    }

    [Fact]
    public void CellText_RawNumber_IsCultureInvariant()
    {
        var row = RowFilter.CellText(12.5, "F1", CultureInfo.GetCultureInfo("de-DE"));

        Assert.Contains("12,5", row);
        Assert.Contains("12.5", row);
    }

    [Theory]
    [InlineData("yyyy-MM-dd HH:mm")]
    [InlineData("{0:yyyy-MM-dd HH:mm}")]
    public void CellText_Date_UsesTheGridFormat_BareOrComposite(string format)
    {
        var text = RowFilter.CellText(new DateTime(2026, 9, 23, 14, 5, 0), format, EnUs);

        Assert.StartsWith("2026-09-23 14:05", text);
    }

    [Fact]
    public void CellText_Boolean_MatchesAsShown()
    {
        Assert.Equal("True", RowFilter.CellText(true, null, EnUs));
    }

    [Fact]
    public void CellText_InvalidFormat_FallsBackToPlainText()
    {
        Assert.StartsWith("42", RowFilter.CellText(42, "{0:N0", EnUs));
    }
}
