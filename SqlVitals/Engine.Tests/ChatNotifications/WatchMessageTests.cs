using System.Text.Json.Nodes;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;
using static SqlVitals.Engine.Tests.ChatNotifications.AlertMessageTests;
using static SqlVitals.Engine.Tests.ChatNotifications.EndMessageTests;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>
/// The messages when SqlVitals stops watching servers and starts again (#170, replacing #123's
/// stopped monitoring): why, for every stop reason; the alerts still active; and the gap.
/// </summary>
public sealed class WatchMessageTests
{
    private static readonly Guid SalesId = Guid.NewGuid();
    private static readonly Guid HrId    = Guid.NewGuid();
    private static readonly DateTime Now = Started.AddHours(3).AddMinutes(12);

    private static StoppedServer Server(string name, WatchStop why, params Alert[] alerts) =>
        new(new StoppedWatching(name == "HR-DB" ? HrId : SalesId, name, why), alerts);

    private static ChatMessage Stopped(params StoppedServer[] servers) =>
        Invariant(() => ChatMessageBuilder.StoppedWatching(servers, "DBA-PC01", Started));

    private static ChatMessage Watching(params WatchedConnection[] servers) =>
        Invariant(() => ChatMessageBuilder.WatchingAgain(servers, "DBA-PC01", Now));

    private static void AssertJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
                    $"Expected:\n{expected}\n\nActual:\n{actual}");

    // ── Stopped watching ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(WatchStop.StoppedByUser,     "because monitoring was stopped in SqlVitals.")]
    [InlineData(WatchStop.AutomaticOff,      "because Start monitoring automatically was turned off.")]
    [InlineData(WatchStop.BackgroundOff,     "because another server was opened, and Monitor in background is off.")]
    [InlineData(WatchStop.NeedsSetup,        "because a saved password or sign-in is missing.")]
    [InlineData(WatchStop.ConnectionRemoved, "because the connection was removed from SqlVitals.")]
    [InlineData(WatchStop.AppExit,           "because the app was closed.")]
    [InlineData(WatchStop.WindowsShutdown,   "because Windows is shutting down.")]
    [InlineData(WatchStop.WindowsSignOut,    "because the Windows user signed out.")]
    [InlineData(WatchStop.Sleep,             "because the PC is going to sleep.")]
    [InlineData(WatchStop.ServiceStopped,    "because the SqlVitals Collector service stopped.")]
    [InlineData(WatchStop.WatchedElsewhere,  "because the SqlVitals Collector service watches it now.")]
    public void Stopped_SaysWhy_ForEachStopReason(WatchStop why, string because)
    {
        var message = Stopped(Server("Sales-Prod", why));

        Assert.Equal("⏹ SqlVitals stopped watching Sales-Prod", message.Title);
        Assert.Equal($"SqlVitals on DBA-PC01 stopped watching Sales-Prod {because} " +
                     "No alerts will be posted for it until SqlVitals is watching again.", message.Text);
    }

    [Fact]
    public void EveryStopReason_HasItsOwnWords()
    {
        var words = Enum.GetValues<WatchStop>().Select(ChatMessageBuilder.Why).ToList();

        Assert.Equal(words.Count, words.Distinct().Count());
        Assert.DoesNotContain("of a change in SqlVitals", words);
    }

    [Fact]
    public void Stopped_SaysWhen_NamesThePcOnce_AndNeverFolds()
    {
        var message = Stopped(Server("Sales-Prod", WatchStop.Sleep));

        Assert.Equal(ChatTone.Neutral, message.Tone);
        var stopped = Assert.Single(message.Facts);
        Assert.Equal(("Stopped", Started), (stopped.Title, stopped.TimeUtc));
        Assert.Null(message.SentFrom); // the text already names the PC
        Assert.False(message.CanFold);
        Assert.Equal("stopped watching Sales-Prod", message.About);
    }

    [Fact]
    public void Stopped_WithAlertsStillActive_NamesThem_CriticalFirst()
    {
        var message = Stopped(Server("Sales-Prod", WatchStop.AppExit, Blocking(), Cpu()));

        Assert.EndsWith(" Sales-Prod had 2 alerts still active: CPU, Blocking.", message.Text);
        Assert.Equal(["CPU", "Blocking"], message.Digest!.Indicators);
    }

    [Fact]
    public void Stopped_WithOneAlertStillActive_SaysAlert()
    {
        Assert.EndsWith(" Sales-Prod had 1 alert still active: CPU.", Stopped(Server("Sales-Prod", WatchStop.AppExit, Cpu())).Text);
    }

    [Fact]
    public void SeveralStoppedForOneReason_AreOneMessage_WithALineOnlyForAlerts()
    {
        var message = Stopped(Server("Sales-Prod", WatchStop.WindowsShutdown, Cpu()), Server("HR-DB", WatchStop.WindowsShutdown));

        Assert.Equal("⏹ SqlVitals stopped watching 2 servers", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 stopped watching Sales-Prod and HR-DB because Windows is shutting down. " +
                     "No alerts will be posted for them until SqlVitals is watching again.", message.Text);
        Assert.Equal([("Sales-Prod", "1 alert still active: CPU"), ("HR-DB", "")],
                     message.Lines.Select(l => (l.Label, l.Text)));
    }

    [Fact]
    public void SeveralStoppedForOneReason_WithNoAlerts_HaveNoLines()
    {
        var message = Stopped(Server("Sales-Prod", WatchStop.Sleep), Server("HR-DB", WatchStop.Sleep));

        Assert.Empty(message.Lines);
        Assert.Equal(0, message.More);
    }

    [Fact]
    public void SeveralStoppedForDifferentReasons_SayEachOnItsLine()
    {
        var message = Stopped(Server("Sales-Prod", WatchStop.StoppedByUser), Server("HR-DB", WatchStop.ConnectionRemoved, Log()));

        Assert.Equal("SqlVitals on DBA-PC01 stopped watching Sales-Prod and HR-DB. " +
                     "No alerts will be posted for them until SqlVitals is watching again.", message.Text);
        Assert.Equal([("Sales-Prod", "stopped because monitoring was stopped in SqlVitals"),
                      ("HR-DB", "stopped because the connection was removed from SqlVitals; 1 alert still active: Log space used")],
                     message.Lines.Select(l => (l.Label, l.Text)));
    }

    [Fact]
    public void Stopped_ForSlack_EscapesNames()
    {
        var json = ChatFormatter.Slack(Stopped(Server("Web<DB>", WatchStop.Sleep), Server("HR-DB", WatchStop.AppExit)));

        Assert.Contains("Web&lt;DB&gt;", json);
        Assert.DoesNotContain("Web<DB>", json);
    }

    [Fact]
    public void Stopped_ForTeams_SaysWhenInTheReadersTimeZone()
    {
        AssertJson("""
            {
              "type": "message",
              "attachments": [{
                "contentType": "application/vnd.microsoft.card.adaptive",
                "contentUrl": null,
                "content": {
                  "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                  "type": "AdaptiveCard",
                  "version": "1.4",
                  "body": [
                    { "type": "TextBlock", "text": "⏹ SqlVitals stopped watching Sales-Prod", "weight": "Bolder", "size": "Medium", "wrap": true },
                    { "type": "TextBlock", "text": "SqlVitals on DBA-PC01 stopped watching Sales-Prod because the PC is going to sleep. No alerts will be posted for it until SqlVitals is watching again.", "wrap": true },
                    { "type": "TextBlock", "text": "**Stopped** {{DATE(2026-09-30T10:42:00Z, SHORT)}} {{TIME(2026-09-30T10:42:00Z)}}", "wrap": true }
                  ]
                }
              }]
            }
            """, ChatFormatter.Teams(Stopped(Server("Sales-Prod", WatchStop.Sleep))));
    }

    // ── Watching again ────────────────────────────────────────────────────────

    [Fact]
    public void WatchingAgain_SaysHowLongItWasntWatched()
    {
        var message = Watching(new WatchedConnection(SalesId, "Sales-Prod", Started));

        Assert.Equal(ChatTone.Good, message.Tone);
        Assert.Equal("▶ SqlVitals is watching Sales-Prod again", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 is watching Sales-Prod again, after 3 h 12 min without monitoring.", message.Text);
        Assert.Equal([("Not watched for", "3 h 12 min", null), ("Last watched", "", Started)],
                     message.Facts.Select(f => (f.Title, f.Value, f.TimeUtc)));
        Assert.False(message.CanFold);
        Assert.Null(message.SentFrom);
    }

    [Fact]
    public void WatchingForTheFirstTime_HasNoGap()
    {
        var message = Watching(new WatchedConnection(SalesId, "Sales-Prod"));

        Assert.Equal("▶ SqlVitals is watching Sales-Prod", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 started watching Sales-Prod.", message.Text);
        Assert.Empty(message.Facts);
    }

    [Fact]
    public void SeveralWatchingAgain_GiveEachGapOnItsLine()
    {
        var message = Watching(new WatchedConnection(SalesId, "Sales-Prod", Now.AddMinutes(-20)),
                               new WatchedConnection(HrId, "HR-DB"));

        Assert.Equal("▶ SqlVitals is watching 2 servers again", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 is watching Sales-Prod and HR-DB again.", message.Text);
        Assert.Equal([("Sales-Prod", "not watched for 20 min", (DateTime?)Now.AddMinutes(-20)), ("HR-DB", "watched for the first time", null)],
                     message.Lines.Select(l => (l.Label, l.Text, l.SinceUtc)));
    }

    [Fact]
    public void SeveralWatchedForTheFirstTime_HaveNoLines()
    {
        var message = Watching(new WatchedConnection(SalesId, "Sales-Prod"), new WatchedConnection(HrId, "HR-DB"));

        Assert.Equal("▶ SqlVitals is watching 2 servers", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 started watching Sales-Prod and HR-DB.", message.Text);
        Assert.Empty(message.Lines);
    }

    [Fact]
    public void ManyServers_ListTenAndSayHowManyMore()
    {
        var servers = Enumerable.Range(1, 12).Select(i => new WatchedConnection(Guid.NewGuid(), $"SQL{i:00}", Started)).ToArray();

        var message = Watching(servers);

        Assert.Equal(ChatMessageBuilder.MaxLines, message.Lines.Count);
        Assert.Equal(2, message.More);
    }

    [Fact]
    public void NothingToPost_IsABug()
    {
        Assert.Throws<ArgumentException>(() => ChatMessageBuilder.StoppedWatching([], "DBA-PC01", Started));
        Assert.Throws<ArgumentException>(() => ChatMessageBuilder.WatchingAgain([], "DBA-PC01", Now));
    }
}
