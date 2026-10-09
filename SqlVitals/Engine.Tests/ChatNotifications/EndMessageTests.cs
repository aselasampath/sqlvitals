using System.Globalization;
using System.Text.Json.Nodes;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;
using static SqlVitals.Engine.Tests.ChatNotifications.AlertMessageTests;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>The messages when alerts end (#123): Back to normal, built and written for both services. Stopped watching is in WatchMessageTests (#170).</summary>
public sealed class EndMessageTests
{
    private static readonly DateTime Ended = Started.AddMinutes(12);

    private const string SlackStarted = "<!date^1790764920^{date_short_pretty} {time}|2026-09-30 10:42 UTC>";
    private const string SlackEnded   = "<!date^1790765640^{date_short_pretty} {time}|2026-09-30 10:54 UTC>";
    private const string TeamsStarted = "{{DATE(2026-09-30T10:42:00Z, SHORT)}} {{TIME(2026-09-30T10:42:00Z)}}";
    private const string TeamsEnded   = "{{DATE(2026-09-30T10:54:00Z, SHORT)}} {{TIME(2026-09-30T10:54:00Z)}}";

    private static Alert Recovered(Alert alert) => alert with { EndedUtc = Ended, EndReason = AlertEndReason.Recovered };

    internal static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ChatMessage BackToNormal(params Alert[] alerts) =>
        Invariant(() => ChatMessageBuilder.Recovered("Sales-Prod", alerts.Select(Recovered).ToList(), "DBA-PC01"));

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

    [Fact]
    public void NothingToPost_IsABug()
    {
        Assert.Throws<ArgumentException>(() => ChatMessageBuilder.Recovered("Sales-Prod", [], "DBA-PC01"));
    }
}
