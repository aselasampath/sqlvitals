using System.Globalization;
using SqlVitals.Engine.Export;

namespace SqlVitals.Engine.Tests.Export;

public class DelimitedTextTests
{
    private static IReadOnlyList<object?>[] Rows(params object?[][] rows) => rows;

    [Fact]
    public void ToCsv_WritesHeaderAndRowsWithCrLf()
    {
        var csv = DelimitedText.ToCsv(["Name", "Count"], Rows(["a", 1], ["b", 2]));

        Assert.Equal("Name,Count\r\na,1\r\nb,2\r\n", csv);
    }

    [Fact]
    public void ToCsv_WithoutHeaders_WritesOnlyRows()
    {
        Assert.Equal("a,1\r\n", DelimitedText.ToCsv(null, Rows(["a", 1])));
    }

    [Theory]
    [InlineData("plain",           "plain")]
    [InlineData("a,b",             "\"a,b\"")]
    [InlineData("say \"hi\"",      "\"say \"\"hi\"\"\"")]
    [InlineData("line1\r\nline2",  "\"line1\r\nline2\"")]
    [InlineData("line1\nline2",    "\"line1\nline2\"")]
    [InlineData(" padded",         "\" padded\"")]
    [InlineData("",                "")]
    public void Quote_Csv_FollowsRfc4180(string field, string expected)
    {
        Assert.Equal(expected, DelimitedText.Quote(field, ','));
    }

    [Fact]
    public void Quote_Tsv_QuotesTabsButNotCommas()
    {
        Assert.Equal("a,b", DelimitedText.Quote("a,b", '\t'));
        Assert.Equal("\"a\tb\"", DelimitedText.Quote("a\tb", '\t'));
    }

    [Fact]
    public void ToTsv_SeparatesWithTabs()
    {
        Assert.Equal("Name\tCount\r\na\t1\r\n", DelimitedText.ToTsv(["Name", "Count"], Rows(["a", 1])));
    }

    [Fact]
    public void Format_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1234.5", DelimitedText.Format(1234.5m));
            Assert.Equal("0.25", DelimitedText.Format(0.25));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Format_Dates()
    {
        Assert.Equal("2026-09-22", DelimitedText.Format(new DateTime(2026, 9, 22)));
        Assert.Equal("2026-09-22 14:05:09", DelimitedText.Format(new DateTime(2026, 9, 22, 14, 5, 9)));
    }

    [Fact]
    public void Format_NullIsEmpty()
    {
        Assert.Equal("", DelimitedText.Format(null));
        Assert.Equal("a,,b\r\n", DelimitedText.ToCsv(null, Rows(["a", null, "b"])));
    }

    [Fact]
    public void CsvEncoding_IsUtf8WithBom()
    {
        Assert.Equal("utf-8", DelimitedText.CsvEncoding.WebName);
        Assert.Equal([0xEF, 0xBB, 0xBF], DelimitedText.CsvEncoding.GetPreamble());
    }
}
