using System.Text.Json.Nodes;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

public sealed class ChatFormatterTests
{
    private static void AssertJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
                    $"Expected:\n{expected}\n\nActual:\n{actual}");

    [Fact]
    public void TheTestMessage_ForSlack_IsBlockKitWithATextFallback()
    {
        var json = ChatFormatter.Format(ChatMessageBuilder.Test("DBA-PC01"), ChatService.Slack);

        AssertJson("""
            {
              "text": "Test message from SqlVitals on DBA-PC01. Alerts will be posted here.",
              "blocks": [
                { "type": "header",
                  "text": { "type": "plain_text", "text": "SqlVitals test message", "emoji": true } },
                { "type": "section",
                  "text": { "type": "mrkdwn", "text": "Test message from SqlVitals on DBA-PC01. Alerts will be posted here." } }
              ]
            }
            """, json);
    }

    [Fact]
    public void TheTestMessage_ForTeams_IsAnAdaptiveCard14_InTheWorkflowEnvelope()
    {
        var json = ChatFormatter.Format(ChatMessageBuilder.Test("DBA-PC01"), ChatService.Teams);

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
                    { "type": "TextBlock", "text": "SqlVitals test message", "weight": "Bolder", "size": "Medium", "wrap": true },
                    { "type": "TextBlock", "text": "Test message from SqlVitals on DBA-PC01. Alerts will be posted here.", "wrap": true }
                  ]
                }
              }]
            }
            """, json);
    }

    [Fact]
    public void Facts_AndTheSentFromLine_AreWrittenForBothServices()
    {
        var message = new ChatMessage(ChatTone.Critical, "🔴 Critical: CPU on Sales-Prod", "",
                                      [new ChatFact("Figure", "CPU 97 % (threshold ≥ 90 %)")], "Sent from SqlVitals on DBA-PC01");

        AssertJson("""
            {
              "text": "",
              "blocks": [
                { "type": "header", "text": { "type": "plain_text", "text": "🔴 Critical: CPU on Sales-Prod", "emoji": true } },
                { "type": "section", "fields": [ { "type": "mrkdwn", "text": "*Figure*\nCPU 97 % (threshold ≥ 90 %)" } ] },
                { "type": "context", "elements": [ { "type": "mrkdwn", "text": "Sent from SqlVitals on DBA-PC01" } ] }
              ]
            }
            """, ChatFormatter.Slack(message));

        var card = JsonNode.Parse(ChatFormatter.Teams(message))!["attachments"]![0]!["content"]!["body"]!.AsArray();
        Assert.Equal("Attention", (string?)card[0]!["color"]);
        Assert.Equal("FactSet", (string?)card[1]!["type"]);
        Assert.Equal("CPU 97 % (threshold ≥ 90 %)", (string?)card[1]!["facts"]![0]!["value"]);
        Assert.Equal("Sent from SqlVitals on DBA-PC01", (string?)card[2]!["text"]);
        Assert.True((bool?)card[2]!["isSubtle"]);
    }

    [Theory]
    [InlineData(ChatTone.Critical, "Attention")]
    [InlineData(ChatTone.Warning, "Warning")]
    [InlineData(ChatTone.Good, "Good")]
    [InlineData(ChatTone.Neutral, null)]
    public void TheTeamsTitleColour_FollowsTheTone(ChatTone tone, string? colour)
    {
        var card = JsonNode.Parse(ChatFormatter.Teams(new ChatMessage(tone, "Title", "Text", [], null)))!;

        Assert.Equal(colour, (string?)card["attachments"]![0]!["content"]!["body"]![0]!["color"]);
    }

    [Fact]
    public void SlackText_IsEscaped_SoANameCantPingTheChannel()
    {
        var message = new ChatMessage(ChatTone.Warning, "Sales <!channel>", "Log full on <!channel> & <@U123>",
                                      [new ChatFact("On", "<http://evil|click>")], "Sent from SqlVitals on <PC>");

        var json = JsonNode.Parse(ChatFormatter.Slack(message))!;

        // Everything Slack parses for mentions and links: the fallback and every mrkdwn text.
        var parsed = new[]
        {
            (string?)json["text"],
            (string?)json["blocks"]![1]!["text"]!["text"],
            (string?)json["blocks"]![2]!["fields"]![0]!["text"],
            (string?)json["blocks"]![3]!["elements"]![0]!["text"],
        };
        Assert.All(parsed, text =>
        {
            Assert.DoesNotContain("<!channel>", text);
            Assert.DoesNotContain("<@U123>", text);
            Assert.DoesNotContain("<http://evil", text);
        });
        Assert.Equal("Log full on &lt;!channel&gt; &amp; &lt;@U123&gt;", parsed[1]);

        // The header is plain_text, which Slack shows as it is and never parses, so it isn't escaped.
        Assert.Equal("Sales <!channel>", (string?)json["blocks"]![0]!["text"]!["text"]);
    }

    [Fact]
    public void ALongTitle_IsCutToSlacksHeaderLimit()
    {
        var json = ChatFormatter.Slack(new ChatMessage(ChatTone.Neutral, new string('x', 400), "", [], null));

        var header = (string?)JsonNode.Parse(json)!["blocks"]![0]!["text"]!["text"];
        Assert.Equal(150, header!.Length);
        Assert.EndsWith("…", header);
    }
}
