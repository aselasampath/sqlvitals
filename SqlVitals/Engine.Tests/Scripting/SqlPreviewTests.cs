using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Tests.Scripting;

/// <summary>
/// Query text in a grid (#232): one line, so a stored procedure's whole batch no longer makes its
/// row taller than the screen, and starting at the code, not at the comment banner above it.
/// </summary>
public class SqlPreviewTests
{
    // A module's batch as sys.dm_exec_sql_text gives it: a banner, the header, a change history in the body.
    private const string Procedure =
        "-- =============================================\r\n" +
        "-- Author:\t\tunicornsts\r\n" +
        "-- Create date: 2019-08-14\r\n" +
        "-- =============================================\r\n" +
        "CREATE PROCEDURE [wf].[GetEntitiesToWakeupWorkflowsByCaseno]\r\n" +
        "\t@EntityId INT = -1,\r\n" +
        "\t@Token INT = -1\r\n" +
        "AS\r\n" +
        "BEGIN\r\n" +
        "\t/* ------------- Change History -------------\r\n" +
        "\t\tunicornadi\t2022-08-16\tchecked stateId\r\n" +
        "\t------------------------------------------- */\r\n" +
        "\tDECLARE @CaseList TABLE ( CaseNo INT )\r\n" +
        "END";

    [Fact]
    public void A_procedure_reads_from_its_CREATE_without_the_banner_or_the_history() =>
        Assert.Equal("CREATE PROCEDURE [wf].[GetEntitiesToWakeupWorkflowsByCaseno] @EntityId INT = -1, @Token INT = -1 AS BEGIN DECLARE @CaseList TABLE ( CaseNo INT ) END",
            SqlPreview.Line(Procedure));

    [Fact]
    public void The_line_has_no_line_breaks_or_tabs() =>
        Assert.DoesNotContain(SqlPreview.Line(Procedure), c => c is '\r' or '\n' or '\t');

    [Fact]
    public void Line_breaks_tabs_and_runs_of_spaces_become_one_space() =>
        Assert.Equal("SELECT a, b FROM t WHERE x = 1", SqlPreview.Line("SELECT\ta,\r\n  b\nFROM   t\rWHERE x = 1"));

    [Fact]
    public void A_comment_at_the_end_of_a_line_goes_and_the_code_after_it_stays() =>
        Assert.Equal("SELECT a FROM t", SqlPreview.Line("SELECT a -- the key\r\nFROM t"));

    [Fact]
    public void A_comment_between_words_still_separates_them() =>
        Assert.Equal("SELECT a FROM t", SqlPreview.Line("SELECT a/*x*/FROM t"));

    [Fact]
    public void Nested_block_comments_end_at_the_outer_close() =>
        Assert.Equal("SELECT 1", SqlPreview.Line("/* outer /* inner */ still a comment */ SELECT 1"));

    [Fact]
    public void An_unclosed_block_comment_runs_to_the_end() =>
        Assert.Equal("SELECT 1", SqlPreview.Line("SELECT 1 /* never closed\r\nSELECT 2"));

    [Theory]
    [InlineData("SELECT '--not a comment' AS a",       "SELECT '--not a comment' AS a")]
    [InlineData("SELECT '/* nor this */' AS a",        "SELECT '/* nor this */' AS a")]
    [InlineData("SELECT 'it''s -- here' AS a -- gone", "SELECT 'it''s -- here' AS a")]
    [InlineData("SELECT N'--x' AS a",                  "SELECT N'--x' AS a")]
    [InlineData("SELECT [a--b], [c]]/*d] FROM t",      "SELECT [a--b], [c]]/*d] FROM t")]
    [InlineData("SELECT \"a/*b\" FROM t",              "SELECT \"a/*b\" FROM t")]
    public void Comment_marks_in_quotes_and_brackets_are_text(string text, string expected) =>
        Assert.Equal(expected, SqlPreview.Line(text));

    [Fact]
    public void A_leading_parameter_list_is_kept() =>
        Assert.Equal("(@p nvarchar(4000))SELECT name FROM sys.databases WHERE name = @p",
            SqlPreview.Line("(@p nvarchar(4000))SELECT\r\n  name\r\nFROM sys.databases\r\nWHERE name = @p"));

    [Fact]
    public void Text_that_is_only_comments_is_shown_with_them() =>
        Assert.Equal("-- just a note /* and this */", SqlPreview.Line("-- just a note\r\n/* and this */"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void Nothing_gives_empty(string? text) => Assert.Equal(string.Empty, SqlPreview.Line(text));

    [Fact]
    public void A_very_long_text_is_cut_short()
    {
        var line = SqlPreview.Line("SELECT " + new string('x', 5000), 100);

        Assert.Equal(101, line.Length);
        Assert.StartsWith("SELECT xxx", line);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void Exactly_the_limit_is_not_cut() =>
        Assert.Equal(new string('x', 100), SqlPreview.Line(new string('x', 100), 100));

    [Fact]
    public void A_huge_comment_before_the_code_is_skipped_not_shown() =>
        Assert.Equal("SELECT 1", SqlPreview.Line("/*" + new string('=', 100_000) + "*/\r\nSELECT 1", 100));

    [Fact]
    public void The_tooltip_keeps_the_line_breaks() =>
        Assert.Equal("SELECT a\r\nFROM t", SqlPreview.Tooltip("\r\n SELECT a\r\nFROM t \r\n"));

    [Fact]
    public void A_huge_tooltip_is_cut_short_and_says_where_the_rest_is()
    {
        var tip = SqlPreview.Tooltip(new string('x', SqlPreview.MaxTooltipLength + 10));

        Assert.StartsWith(new string('x', SqlPreview.MaxTooltipLength), tip);
        Assert.EndsWith("(View shows all of it)", tip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void No_text_gives_no_tooltip(string? text) => Assert.Equal(string.Empty, SqlPreview.Tooltip(text));
}
