using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Tests.History;

/// <summary>Query text without its values (#175): the shape is kept, every literal becomes ?.</summary>
public sealed class SqlLiteralsTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Users WHERE Email = 'ann@example.com'",
                "SELECT * FROM dbo.Users WHERE Email = ?")]
    [InlineData("SELECT * FROM dbo.Users WHERE Name = N'Zoë' AND Note = 'it''s'",
                "SELECT * FROM dbo.Users WHERE Name = ? AND Note = ?")]
    [InlineData("SELECT TOP 10 * FROM dbo.Orders WHERE Total > 99.95 AND Qty >= 3 AND Rate < 1.5E3",
                "SELECT TOP ? * FROM dbo.Orders WHERE Total > ? AND Qty >= ? AND Rate < ?")]
    [InlineData("UPDATE dbo.Accounts SET Balance = $12.50, Hash = 0x1F2E WHERE Id = -7",
                "UPDATE dbo.Accounts SET Balance = ?, Hash = ? WHERE Id = -?")]
    public void StringsNumbersMoneyAndBinary_BecomeQuestionMarks(string sql, string expected) =>
        Assert.Equal(expected, SqlLiterals.Remove(sql));

    [Fact]
    public void AnInList_KeepsOneMarkPerValue()
    {
        Assert.Equal("SELECT * FROM t WHERE Id IN (?, ?, ?) AND Code IN (?,?)",
                     SqlLiterals.Remove("SELECT * FROM t WHERE Id IN (1, 2, 3) AND Code IN ('a','b')"));
    }

    [Fact]
    public void NamesVariablesTempTablesAndComments_AreKept()
    {
        const string sql = "/* nightly */ SELECT [Order 2].Col1, @p1, #t1.x FROM Table1 AS [Order 2] -- by id\r\nJOIN #t1 ON 1 = 1";

        Assert.Equal("/* nightly */ SELECT [Order 2].Col1, @p1, #t1.x FROM Table1 AS [Order 2] -- by id\r\nJOIN #t1 ON ? = ?",
                     SqlLiterals.Remove(sql));
    }

    [Fact]
    public void AParameterisedStatement_IsKeptAsItIs()
    {
        const string sql = "(@p0 int,@p1 nvarchar(4000))SELECT Name FROM dbo.Users WHERE Id = @p0 AND Email = @p1";

        Assert.Equal("(@p0 int,@p1 nvarchar(?))SELECT Name FROM dbo.Users WHERE Id = @p0 AND Email = @p1", SqlLiterals.Remove(sql));
    }

    [Fact]
    public void DynamicSqlOfSpExecuteSql_IsKept_WithItsOwnLiteralsReplaced()
    {
        const string sql = "EXEC sp_executesql N'SELECT * FROM t WHERE Name = ''bob'' AND Id = @id', N'@id int', @id = 5";

        Assert.Equal("EXEC sp_executesql N'SELECT * FROM t WHERE Name = ? AND Id = @id', ?, @id = ?", SqlLiterals.Remove(sql));
    }

    [Fact]
    public void DynamicSqlNamedStmt_AndOfExec_IsKept()
    {
        Assert.Equal("EXECUTE sys.sp_executesql @stmt = N'DELETE FROM t WHERE Id = ?'",
                     SqlLiterals.Remove("EXECUTE sys.sp_executesql @stmt = N'DELETE FROM t WHERE Id = 42'"));
        Assert.Equal("EXEC ('UPDATE t SET Pwd = ? WHERE Login = ?')",
                     SqlLiterals.Remove("EXEC ('UPDATE t SET Pwd = ''s3cret'' WHERE Login = ''sa''')"));
    }

    [Fact]
    public void AnyOtherString_IsAValue_EvenWhenItLooksLikeSql()
    {
        Assert.Equal("DECLARE @sql nvarchar(max) = ?; SET @note = ?",
                     SqlLiterals.Remove("DECLARE @sql nvarchar(max) = N'SELECT 1'; SET @note = 'Delete John Smith'"));
    }

    [Fact]
    public void TextCutOffInsideAString_LeavesNoneOfIt()
    {
        // History keeps at most 4,000 characters: the cut can fall inside a literal.
        var cut = SqlLiterals.Remove("SELECT * FROM dbo.Users WHERE Email = 'ann@exam");

        Assert.Equal("SELECT * FROM dbo.Users WHERE Email = ?", cut);
    }

    [Fact]
    public void TheFallback_ReplacesMoreRatherThanLess()
    {
        Assert.Equal("SELECT a1, @p2, #t3 FROM t WHERE x = ? AND y = ? AND z = ? AND w = ?",
                     SqlLiterals.Fallback("SELECT a1, @p2, #t3 FROM t WHERE x = 'it''s' AND y = N'é' AND z = 0xFF AND w = 12.5"));
    }

    [Fact]
    public void TheFallback_ReadsPastAnApostropheInACommentOrAName()
    {
        // Cut off, so the lexer can't read it: the fallback must still find both strings.
        const string sql = "-- customer's lookup\r\nSELECT [o'brien].x FROM c WHERE email = 'ann@x.com' AND name = 'Bo";

        var cleaned = SqlLiterals.Remove(sql);

        Assert.Equal("-- customer's lookup\r\nSELECT [o'brien].x FROM c WHERE email = ? AND name = ?", cleaned);
    }

    [Fact]
    public void DoubleQuotedText_GoesToo_SinceWithQuotedIdentifierOffItIsAString()
    {
        Assert.Equal("SELECT * FROM t WHERE Email = ? AND Pwd = ?",
                     SqlLiterals.Remove("SELECT * FROM t WHERE Email = \"ann@example.com\" AND Pwd = \"x\""));
        Assert.Equal("SELECT * FROM t WHERE Note = ?", SqlLiterals.Fallback("SELECT * FROM t WHERE Note = \"it's"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_IsNothing(string? sql) => Assert.Equal(string.Empty, SqlLiterals.Remove(sql));
}
