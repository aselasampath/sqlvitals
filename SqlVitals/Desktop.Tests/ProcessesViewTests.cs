using System.IO;
using System.Text.Json.Nodes;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>The Processes grid (#116): its remembered view, and what its rows show.</summary>
public sealed class ProcessesViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Create() => new(_dir);

    [Fact]
    public void ANewInstallation_ShowsTheMap_GroupedByLoginWhenTheGridIsPicked()
    {
        var store = Create().Load();

        Assert.Equal(ProcessesView.Map, store.ProcessesView);
        Assert.True(store.ProcessesGroupByLogin);
    }

    [Fact]
    public void TheView_SurvivesSaveAndLoad()
    {
        Create().SetProcessesView(ProcessesView.Grid, groupByLogin: false);

        var store = Create().Load();
        Assert.Equal(ProcessesView.Grid, store.ProcessesView);
        Assert.False(store.ProcessesGroupByLogin);
    }

    [Fact]
    public void AFileFromBeforeTheGrid_ReadsAsTheMap_GroupedByLogin()
    {
        Create().SetKeepRunningInTray(false);
        var file = Path.Combine(_dir, "settings.dat");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json.Remove("ProcessesView");
        json.Remove("ProcessesGroupByLogin");
        File.WriteAllText(file, json.ToJsonString());

        var store = Create().Load();
        Assert.Equal(ProcessesView.Map, store.ProcessesView);
        Assert.True(store.ProcessesGroupByLogin);
    }

    [Theory]
    [InlineData("Grid", ProcessesView.Grid)]
    [InlineData("grid", ProcessesView.Grid)]
    [InlineData("Map", ProcessesView.Map)]
    [InlineData("Table", ProcessesView.Map)]
    [InlineData("7", ProcessesView.Map)]
    [InlineData(null, ProcessesView.Map)]
    public void ParseProcessesView_FallsBackToTheMap(string? raw, ProcessesView expected) =>
        Assert.Equal(expected, ConnectionStore.ParseProcessesView(raw));

    [Fact]
    public void ChangingTheView_IsInTheActivityLog()
    {
        var service = Create();
        var raised  = new List<ActivityEntry>();
        service.Changed += raised.AddRange;

        service.SetProcessesView(ProcessesView.Grid, groupByLogin: true);
        service.SetProcessesView(ProcessesView.Grid, groupByLogin: false);

        Assert.Collection(raised,
            r => Assert.Equal(("Processes view", "Map", "Grid", "user: Processes"), (r.Setting, r.OldValue, r.NewValue, r.Source)),
            r => Assert.Equal(("Processes group by login", "On", "Off"), (r.Setting, r.OldValue, r.NewValue)));
    }

    // ── Rows ─────────────────────────────────────────────────────────────

    private static ProcessNode Node(int spid, int blockedBy = 0, string state = BlockingChains.Active,
                                    string? text = null, string? waitType = null, long waitMs = 0) =>
        new(spid, blockedBy, "running", "app", "host", "prog", waitType, waitMs, null, null, "db",
            0, 0, 0, DateTime.MinValue, DateTime.MinValue, 0, null, text, state);

    private static ProcessGridRow Row(ProcessNode node, int head, int blocking = 0, bool blockerVisible = true) =>
        new(node, new ProcessGroupHeader("APP"), head, blocking, blockerVisible);

    [Fact]
    public void ASessionOutsideAChain_HasNoHeadNoBlockingAndNoBlocker()
    {
        var row = Row(Node(51), head: 51);

        Assert.Equal((null, null, null, false), (row.HeadOfChain, row.Blocking, row.BlockedBy, row.HeadIsLink));
    }

    [Fact]
    public void AHeadBlocker_IsItsOwnHead_NotALink()
    {
        var row = Row(Node(51, state: BlockingChains.HeadBlocker), head: 51, blocking: 2);

        Assert.Equal((51, 2), (row.HeadOfChain, row.Blocking));
        Assert.False(row.HeadIsLink);
        Assert.Equal("Head blocker", row.State);
    }

    [Fact]
    public void ABlockedSession_LinksToItsBlockerAndHead()
    {
        var row = Row(Node(70, blockedBy: 61, state: BlockingChains.Blocked), head: 55);

        Assert.Equal((61, true, 55, true), (row.BlockedBy, row.BlockedByIsLink, row.HeadOfChain, row.HeadIsLink));
        Assert.Equal("Blocked by SPID 61", row.BlockedByTooltip);
    }

    [Fact]
    public void ABlockerThatIsntASession_IsShownButNotALink()
    {
        var row = Row(Node(70, blockedBy: -2, state: BlockingChains.Blocked), head: 70, blockerVisible: false);

        Assert.Equal(-2, row.BlockedBy);
        Assert.False(row.BlockedByIsLink);
        Assert.Contains("orphaned distributed transaction", row.BlockedByTooltip);
    }

    [Fact]
    public void ASessionWithNoLogin_SaysSoInTheFlatList()
    {
        var node = Node(51) with { UserName = null };

        Assert.Equal(ProcessGroups.NoLogin, Row(node, head: 51).Login);
        Assert.Equal("app", Row(Node(52), head: 52).Login);
    }

    [Fact]
    public void Wait_IsInSeconds_AndEmptyWithoutAWait()
    {
        Assert.Null(Row(Node(51), head: 51).WaitSec);
        Assert.Equal(1.5, Row(Node(51, waitType: "LCK_M_X", waitMs: 1_500), head: 51).WaitSec);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("SELECT 1", "SELECT 1")]
    [InlineData("\r\n\r\n  UPDATE t SET x = 1\r\n  WHERE id = 2", "UPDATE t SET x = 1")]
    public void QueryText_ShowsTheFirstLineWithText(string? text, string expected) =>
        Assert.Equal(expected, ProcessGridRow.FirstLine(text));

    [Fact]
    public void QueryText_KeepsVeryLongTextOutOfTheCellAndTooltip()
    {
        var text = new string('x', 20_000);
        var row  = Row(Node(51, text: text), head: 51);

        Assert.Equal(ProcessGridRow.MaxFirstLineChars + 1, row.QueryFirstLine.Length);
        Assert.Equal(ProcessGridRow.MaxTooltipChars + 1, row.QueryTooltip!.Length);
        Assert.Null(Row(Node(52), head: 52).QueryTooltip);
    }

    [Fact]
    public void GroupHeader_TakesTheGroupsNameAndSummary_AndStartsExpanded()
    {
        var group  = ProcessGroups.ByLogin(BlockingChains.Classify(
            [Node(51, blockedBy: 52), Node(52)]))[0];
        var header = new ProcessGroupHeader(group.Key);
        var changed = new List<string?>();
        header.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        header.Update(group);

        Assert.True(header.IsExpanded);
        Assert.Equal("app", header.Name);
        Assert.True(header.HasHeadBlocker);
        Assert.True(header.HasBlocked);
        Assert.Contains("1 head blocker", header.Summary);
        Assert.Contains(nameof(ProcessGroupHeader.Summary), changed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
