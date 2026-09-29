using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Engine.Tests.QueryStore;

public class QueryStoreTextTests
{
    [Fact]
    public void Line_breaks_become_one_space() =>
        Assert.Equal("SELECT s.session_id AS SessionId, s.login_name FROM sys.dm_exec_sessions s",
            QueryStoreText.OneLine("SELECT\r\n  s.session_id AS SessionId,\r\n  s.login_name\nFROM sys.dm_exec_sessions s"));

    [Fact]
    public void Tabs_and_runs_of_spaces_collapse() =>
        Assert.Equal("SELECT a, b FROM t", QueryStoreText.OneLine("SELECT\ta,\t\t b    FROM   t"));

    [Fact]
    public void A_leading_parameter_list_is_kept() =>
        Assert.Equal("(@p nvarchar(4000))SELECT name FROM sys.databases WHERE name = @p",
            QueryStoreText.OneLine("(@p nvarchar(4000))SELECT\r\n  name\r\nFROM sys.databases\r\nWHERE name = @p"));

    [Fact]
    public void Leading_and_trailing_whitespace_goes() =>
        Assert.Equal("SELECT 1", QueryStoreText.OneLine("\r\n   SELECT 1 \r\n"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void Nothing_gives_empty(string? text) => Assert.Equal(string.Empty, QueryStoreText.OneLine(text));

    [Fact]
    public void A_very_long_statement_is_cut_short()
    {
        var line = QueryStoreText.OneLine(new string('x', 5000), 100);

        Assert.Equal(101, line.Length);
        Assert.EndsWith("…", line);
    }

    [Theory]
    [InlineData(0,  "dbo.GetOrders", "ad hoc")]
    [InlineData(42, "dbo.GetOrders", "dbo.GetOrders")]
    [InlineData(42, null,            "object 42")]
    public void The_object_is_named_or_ad_hoc(long objectId, string? name, string expected) =>
        Assert.Equal(expected, QueryStoreText.ObjectLabel(objectId, name));

    [Fact]
    public void The_unforce_script_names_the_query_and_plan() =>
        Assert.Equal("EXEC sys.sp_query_store_unforce_plan @query_id = 12, @plan_id = 34;", QueryStoreText.UnforceScript(12, 34));

    [Theory]
    [InlineData("AUTO",   "Automatic plan correction")]
    [InlineData("MANUAL", "Manual")]
    [InlineData(null,     "Manual")]
    public void Forced_by_reads_in_words(string? forcingType, string expected) =>
        Assert.Equal(expected, QueryStoreText.ForcedBy(forcingType));
}
