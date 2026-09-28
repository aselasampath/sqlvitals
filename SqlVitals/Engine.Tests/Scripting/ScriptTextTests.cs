using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;
using SqlVitals.Engine.Security;

namespace SqlVitals.Engine.Tests.Scripting;

/// <summary>
/// Names from the server placed in a generated script's comments (#109). A name can hold a line
/// break, which would end a -- comment and run the rest of the name as T-SQL.
/// </summary>
public class ScriptTextTests
{
    private const string Hostile = "x\nDROP DATABASE [Sales];--";
    private const string Payload = "DROP DATABASE";
    private static readonly DateTime Now = new(2026, 9, 28, 10, 0, 0);

    // Every line that holds the payload is a comment.
    private static void AssertPayloadOnlyInComments(string script) =>
        Assert.All(script.Split('\n').Where(line => line.Contains(Payload)),
                   line => Assert.StartsWith("--", line.TrimStart()));

    [Fact]
    public void OneLine_turns_every_kind_of_line_break_into_a_space()
    {
        var text = "a\rb\nc" + (char)0x85 + "d" + (char)0x2028 + "e" + (char)0x2029 + "f";

        Assert.Equal("a b c d e f", ScriptText.OneLine(text));
    }

    [Fact]
    public void Comment_keeps_the_text_on_one_comment_line()
    {
        Assert.Equal("-- [x DROP DATABASE [Sales];--]", ScriptText.Comment("[" + Hostile + "]"));
        Assert.Equal("-- plain", ScriptText.Comment("plain"));
    }

    [Fact]
    public void An_orphaned_users_name_stays_in_the_commented_out_statements()
    {
        var fix = SecurityAudit.OrphanFix(new OrphanedUser("Sales", Hostile, [1, 2, 3]), login: null);

        AssertPayloadOnlyInComments(fix);
        Assert.Contains("-- DROP USER [x DROP DATABASE [Sales]];--];", fix);
    }

    [Fact]
    public void A_security_findings_subject_stays_in_its_comment_in_the_combined_script()
    {
        var finding = new SecurityFinding(SecurityCheckKind.SysadminMember, "Server", Hostile, SecurityFindingStatus.Risk,
            "SQL login · enabled", "why", "-- review\nSELECT 1;");
        var script = new SecurityFindingList([finding], null).FixScript("PROD01", Now)!;

        AssertPayloadOnlyInComments(script);
    }

    [Fact]
    public void A_config_checks_database_stays_in_its_comment_in_the_combined_script()
    {
        var check = new ConfigCheck(ConfigCheckKind.AutoShrink, Hostile, ConfigCheckStatus.Warn, "ON", "OFF", "why", "SELECT 1;");
        var script = ConfigCheckList.BuildScript([check], "PROD01", Now)!;

        AssertPayloadOnlyInComments(script);
    }

    [Fact]
    public void A_unique_index_left_commented_out_stays_commented_out()
    {
        var index = new UnusedIndex("Sales", "dbo", "Orders", Hostile, "NONCLUSTERED", true, false, false,
            0, 0, 0, 10, null, null, null, 0, 5000, Now);

        AssertPayloadOnlyInComments(UnusedIndexDropScript.Build([index], Now));
    }

    [Fact]
    public void A_skipped_heaps_rebuild_stays_commented_out()
    {
        var heap = new IndexFragmentation("dbo", Hostile, null, "HEAP", 80, 5000, 70, 1000, "Sales", "Rebuild", Now);

        AssertPayloadOnlyInComments(IndexMaintenanceScript.Build([heap], new IndexMaintenanceOptions(), Now));
    }

    [Fact]
    public void A_skipped_missing_index_suggestion_stays_in_its_comment()
    {
        var suggestion = new MissingIndex("Sales", "dbo", Hostile, null, null, null,
            10, 500, 20, Now, null, 12.5, 90, 5625, "CRITICAL", "", Now);

        AssertPayloadOnlyInComments(MissingIndexCreateScript.Build([suggestion], Now));
    }

    [Fact]
    public void A_statistics_name_stays_on_its_comment_line()
    {
        var stat = new StaleStatistic("dbo", "Orders", Hostile, null, null, null, null, 0, 10, false, false, false, "Sales", Now);
        var script = UpdateStatisticsScript.Build([stat], StatisticsSampling.Default, Now);

        Assert.Contains("-- [dbo].[Orders].[x DROP DATABASE [Sales]];--]", script);
        Assert.Contains("Rows: unknown", script);
    }
}
