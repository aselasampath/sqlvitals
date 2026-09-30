using System.Globalization;
using System.Text.Json.Nodes;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;
using static SqlVitals.Engine.Tests.ChatNotifications.AlertMessageTests;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>The messages when alerts end (#123): Back to normal, and stopped monitoring, built and written for both services.</summary>
public sealed class EndMessageTests
{
    private static readonly DateTime Ended = Started.AddMinutes(12);

    private const string SlackStarted = "<!date^1790764920^{date_short_pretty} {time}|2026-09-30 10:42 UTC>";
    private const string SlackEnded   = "<!date^1790765640^{date_short_pretty} {time}|2026-09-30 10:54 UTC>";
    private const string TeamsStarted = "{{DATE(2026-09-30T10:42:00Z, SHORT)}} {{TIME(2026-09-30T10:42:00Z)}}";
    private const string TeamsEnded   = "{{DATE(2026-09-30T10:54:00Z, SHORT)}} {{TIME(2026-09-30T10:54:00Z)}}";

    private static Alert Recovered(Alert alert) => alert with { EndedUtc = Ended, EndReason = AlertEndReason.Recovered };

    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ChatMessage BackToNormal(params Alert[] alerts) =>
        Invariant(() => ChatMessageBuilder.Recovered("Sales-Prod", alerts.Select(Recovered).ToList(), "DBA-PC01"));

    private static ChatMessage Stopped(params StoppedConnection[] connections) =>
        Invariant(() => ChatMessageBuilder.Stopped(connections, "DBA-PC01"));

    private static void AssertJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
                    $"Expected:\n{expected}\n\nActual:\n{actual}");

    // ── Back to normal ────────────────────────────────────────────────────────

    [Fact]
    public void BackToNormal_SaysHowLongItLasted_ItsWorst_AndWhenItStartedAndEnded()
    {
        var message = BackToNormal(Cpu());

        Assert.Equal(ChatTone.Good, message.Tone);
        Assert.Equal("✅ Back to normal: CPU on Sales-Prod", message.Title);
        Assert.Equal(
            [new ChatFact("Worst", "97 % (threshold ≥ 90 %)"), new ChatFact("Lasted", "12 min"),
             new ChatFact("Started", "", Started), new ChatFact("Ended", "", Ended)],
            message.Facts);
        Assert.Equal("end of alert on Sales-Prod", message.About);
    }

    [Fact]
    public void BackToNormal_ForSlack()
    {
        AssertJson($$"""
            {
              "text": "✅ Back to normal: CPU on Sales-Prod. Worst 97 % (threshold ≥ 90 %), lasted 12 min.",
              "blocks": [
                { "type": "header", "text": { "type": "plain_text", "text": "✅ Back to normal: CPU on Sales-Prod", "emoji": true } },
                { "type": "section", "fields": [
                  { "type": "mrkdwn", "text": "*Worst*\n97 % (threshold ≥ 90 %)" },
                  { "type": "mrkdwn", "text": "*Lasted*\n12 min" },
                  { "type": "mrkdwn", "text": "*Started*\n{{SlackStarted}}" },
                  { "type": "mrkdwn", "text": "*Ended*\n{{SlackEnded}}" } ] },
                { "type": "context", "elements": [ { "type": "mrkdwn", "text": "Sent from SqlVitals on DBA-PC01" } ] }
              ]
            }
            """, ChatFormatter.Slack(BackToNormal(Cpu())));
    }

    [Fact]
    public void BackToNormal_ForTeams_HasTheGoodColour()
    {
        AssertJson($$"""
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
                    { "type": "TextBlock", "text": "✅ Back to normal: CPU on Sales-Prod", "weight": "Bolder", "size": "Medium", "color": "Good", "wrap": true },
                    { "type": "FactSet", "facts": [
                      { "title": "Worst", "value": "97 % (threshold ≥ 90 %)" },
                      { "title": "Lasted", "value": "12 min" } ] },
                    { "type": "TextBlock", "text": "**Started** {{TeamsStarted}}", "wrap": true },
                    { "type": "TextBlock", "text": "**Ended** {{TeamsEnded}}", "wrap": true },
                    { "type": "TextBlock", "text": "Sent from SqlVitals on DBA-PC01", "isSubtle": true, "size": "Small", "wrap": true }
                  ]
                }
              }]
            }
            """, ChatFormatter.Teams(BackToNormal(Cpu())));
    }

    [Fact]
    public void BackToNormal_ForBlocking_KeepsItsDetail()
    {
        var facts = BackToNormal(Blocking()).Facts;

        Assert.Contains(new ChatFact("Detail", "Blocking 3.5 min (4 blocked)"), facts);
        // Started 3 minutes before the others: lasted 15.
        Assert.Contains(new ChatFact("Lasted", "15 min"), facts);
    }

    [Fact]
    public void SeveralBackToNormal_AreLinesInOneMessage_CriticalFirst()
    {
        var message = BackToNormal(Blocking(), Cpu());

        Assert.Equal("✅ 2 alerts back to normal on Sales-Prod", message.Title);
        Assert.Equal(
            [new ChatLine(ChatTone.Good, "CPU", "worst 97 % (threshold ≥ 90 %), lasted 12 min"),
             new ChatLine(ChatTone.Good, "Blocking", "worst 3.5 min (threshold ≥ 1 min), lasted 15 min")],
            message.Lines);
        Assert.Equal([new ChatFact("Ended", "", Ended)], message.Facts);
        Assert.Equal("✅ 2 alerts back to normal on Sales-Prod: CPU and Blocking.", message.FallbackText);

        var slack = (string?)JsonNode.Parse(ChatFormatter.Slack(message))!["blocks"]![2]!["text"]!["text"];
        Assert.Equal("✅ *CPU* worst 97 % (threshold ≥ 90 %), lasted 12 min\n✅ *Blocking* worst 3.5 min (threshold ≥ 1 min), lasted 15 min", slack);
    }

    // ── Stopped monitoring ────────────────────────────────────────────────────

    [Fact]
    public void Stopped_NamesThePcTheConnectionAndItsAlerts_AndSaysNoMoreUpdatesAreComing()
    {
        var message = Stopped(new StoppedConnection("Sales-Prod", [Blocking(), Cpu()]));

        Assert.Equal(ChatTone.Neutral, message.Tone);
        Assert.Equal("⏹ Stopped monitoring Sales-Prod", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 stopped monitoring Sales-Prod with 2 alerts still active (CPU, Blocking). " +
                     "No further updates will be posted.", message.Text);
        Assert.Null(message.SentFrom); // the text already names the PC
        Assert.False(message.CanFold);
    }

    [Fact]
    public void Stopped_WithOneAlert_SaysAlert()
    {
        Assert.Contains("with 1 alert still active (CPU).", Stopped(new StoppedConnection("Sales-Prod", [Cpu()])).Text);
    }

    [Fact]
    public void Stopped_ForSlack()
    {
        AssertJson("""
            {
              "text": "SqlVitals on DBA-PC01 stopped monitoring Sales-Prod with 1 alert still active (CPU). No further updates will be posted.",
              "blocks": [
                { "type": "header", "text": { "type": "plain_text", "text": "⏹ Stopped monitoring Sales-Prod", "emoji": true } },
                { "type": "section", "text": { "type": "mrkdwn", "text": "SqlVitals on DBA-PC01 stopped monitoring Sales-Prod with 1 alert still active (CPU). No further updates will be posted." } }
              ]
            }
            """, ChatFormatter.Slack(Stopped(new StoppedConnection("Sales-Prod", [Cpu()]))));
    }

    [Fact]
    public void Stopped_ForTeams_HasTheDefaultColour()
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
                    { "type": "TextBlock", "text": "⏹ Stopped monitoring Sales-Prod", "weight": "Bolder", "size": "Medium", "wrap": true },
                    { "type": "TextBlock", "text": "SqlVitals on DBA-PC01 stopped monitoring Sales-Prod with 1 alert still active (CPU). No further updates will be posted.", "wrap": true }
                  ]
                }
              }]
            }
            """, ChatFormatter.Teams(Stopped(new StoppedConnection("Sales-Prod", [Cpu()]))));
    }

    [Fact]
    public void StoppedAtExit_NamesEveryConnectionInOneMessage()
    {
        var message = Stopped(new StoppedConnection("Sales-Prod", [Cpu(), Blocking()]),
                              new StoppedConnection("HR-DB", [Log()]),
                              new StoppedConnection("Web<DB>", [Cpu(HealthLevel.Warning)]));

        Assert.Equal("⏹ Stopped monitoring 3 connections", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 stopped monitoring Sales-Prod (CPU, Blocking), HR-DB (Log space used) and Web<DB> (CPU), " +
                     "with alerts still active. No further updates will be posted.", message.Text);
        Assert.Equal("stopped monitoring 3 connections", message.About);
        // Escaped for Slack, so a name can't become a link or a mention.
        Assert.Contains("Web&lt;DB&gt; (CPU)", ChatFormatter.Slack(message));
    }

    [Fact]
    public void NothingToPost_IsABug()
    {
        Assert.Throws<ArgumentException>(() => ChatMessageBuilder.Recovered("Sales-Prod", [], "DBA-PC01"));
        Assert.Throws<ArgumentException>(() => ChatMessageBuilder.Stopped([], "DBA-PC01"));
    }
}
