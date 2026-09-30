using System.Globalization;
using System.Text.Json.Nodes;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>The message for alerts that started or turned critical (#121), built and written for both services.</summary>
public sealed class AlertMessageTests
{
    internal static readonly Guid     Connection = Guid.Parse("00000000-0000-4000-8000-0000000000c1");
    internal static readonly DateTime Started    = new(2026, 9, 30, 10, 42, 0, DateTimeKind.Utc);

    private const string SlackStarted = "<!date^1790764920^{date_short_pretty} {time}|2026-09-30 10:42 UTC>";
    private const string TeamsStarted = "{{DATE(2026-09-30T10:42:00Z, SHORT)}} {{TIME(2026-09-30T10:42:00Z)}}";

    internal static Alert Cpu(HealthLevel severity = HealthLevel.Critical) =>
        new(Guid.NewGuid(), Connection, HealthIndicator.Cpu, severity, Started, null, 97, severity == HealthLevel.Critical ? 90 : 80, "CPU 97%");

    internal static Alert Blocking(HealthLevel severity = HealthLevel.Warning) =>
        new(Guid.NewGuid(), Connection, HealthIndicator.Blocking, severity, Started.AddMinutes(-3), null, 212, 60, "Blocking 3.5 min (4 blocked)");

    internal static Alert Log() =>
        new(Guid.NewGuid(), Connection, HealthIndicator.LogSpace, HealthLevel.Critical, Started, null, 93, 90, "Log 93% full (Sales)");

    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ChatMessage Raised(params RaisedAlert[] alerts) =>
        Invariant(() => ChatMessageBuilder.Raised("Sales-Prod", alerts, "DBA-PC01"));

    private static void AssertJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
                    $"Expected:\n{expected}\n\nActual:\n{actual}");

    // ── Built ─────────────────────────────────────────────────────────────────

    [Fact]
    public void OneAlert_HasItsSeverityConnectionAndIndicatorInTheTitle_AndItsFigureAndStartAsFacts()
    {
        var message = Raised(new RaisedAlert(Cpu()));

        Assert.Equal(ChatTone.Critical, message.Tone);
        Assert.Equal("🔴 Critical: CPU on Sales-Prod", message.Title);
        Assert.Equal(
            [new ChatFact("Figure", "97 % (threshold ≥ 90 %)"), new ChatFact("Started", "", Started)],
            message.Facts);
        Assert.Equal("Sent from SqlVitals on DBA-PC01", message.SentFrom);
        Assert.Equal("🔴 Critical: CPU on Sales-Prod. CPU 97 % (threshold ≥ 90 %).", message.FallbackText);
        Assert.Equal("alert on Sales-Prod", message.About);
    }

    [Fact]
    public void AWarning_IsOrange()
    {
        var message = Raised(new RaisedAlert(Cpu(HealthLevel.Warning)));

        Assert.Equal(ChatTone.Warning, message.Tone);
        Assert.Equal("🟠 Warning: CPU on Sales-Prod", message.Title);
    }

    [Fact]
    public void AWarningThatTurnedCritical_SaysNowCritical()
    {
        Assert.Equal("🔴 Now critical: CPU on Sales-Prod", Raised(new RaisedAlert(Cpu(), NowCritical: true)).Title);
    }

    [Fact]
    public void TheHealthDotsDetail_IsAddedWhenItSaysMore_LikeTheDatabaseWhoseLogIsFull()
    {
        var log = Raised(new RaisedAlert(Log()));
        var cpu = Raised(new RaisedAlert(Cpu()));

        Assert.Contains(new ChatFact("Detail", "Log 93% full (Sales)"), log.Facts);
        Assert.DoesNotContain(cpu.Facts, f => f.Title == "Detail");   // "CPU 97%" only repeats the figure
    }

    [Fact]
    public void SeveralAlerts_ShareOneMessage_CriticalFirst_WithALineEach()
    {
        var message = Raised(new RaisedAlert(Blocking()), new RaisedAlert(Cpu()));

        Assert.Equal("🔴 2 alerts on Sales-Prod (1 critical)", message.Title);
        Assert.Empty(message.Facts);
        Assert.Collection(message.Lines,
            l => Assert.Equal(new ChatLine(ChatTone.Critical, "CPU", "97 % (threshold ≥ 90 %)", Started), l),
            l => Assert.Equal(new ChatLine(ChatTone.Warning, "Blocking", "3.5 min (threshold ≥ 1 min) · Blocking 3.5 min (4 blocked)",
                                           Started.AddMinutes(-3)), l));
        Assert.Equal("🔴 2 alerts on Sales-Prod (1 critical): CPU and Blocking.", message.FallbackText);
        Assert.Equal(0, message.More);
    }

    [Fact]
    public void SeveralWarnings_AreOrange_WithoutACriticalCount()
    {
        Assert.Equal("🟠 2 alerts on Sales-Prod",
                     Raised(new RaisedAlert(Blocking()), new RaisedAlert(Cpu(HealthLevel.Warning))).Title);
    }

    [Fact]
    public void MoreThanTenAlerts_AreCutAfterTen_AndCounted()
    {
        var alerts = Enumerable.Range(0, 13).Select(_ => new RaisedAlert(Cpu())).ToArray();

        var message = Raised(alerts);

        Assert.Equal(10, message.Lines.Count);
        Assert.Equal(3, message.More);
    }

    [Fact]
    public void TheTestMessage_NamesTheConnections()
    {
        Assert.Equal("Test message from SqlVitals on DBA-PC01. Alerts for Sales-Prod and HR-DB will be posted here.",
                     ChatMessageBuilder.Test("DBA-PC01", "Sales-Prod and HR-DB").Text);
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "A" }, "A")]
    [InlineData(new[] { "A", "B" }, "A and B")]
    [InlineData(new[] { "A", "B", "C" }, "A, B and C")]
    public void List_ReadsAsASentence(string[] items, string expected) => Assert.Equal(expected, ChatMessageBuilder.List(items));

    // ── Written ───────────────────────────────────────────────────────────────

    [Fact]
    public void OneAlert_ForSlack()
    {
        AssertJson($$"""
            {
              "text": "🔴 Critical: CPU on Sales-Prod. CPU 97 % (threshold ≥ 90 %).",
              "blocks": [
                { "type": "header", "text": { "type": "plain_text", "text": "🔴 Critical: CPU on Sales-Prod", "emoji": true } },
                { "type": "section", "fields": [
                  { "type": "mrkdwn", "text": "*Figure*\n97 % (threshold ≥ 90 %)" },
                  { "type": "mrkdwn", "text": "*Started*\n{{SlackStarted}}" } ] },
                { "type": "context", "elements": [ { "type": "mrkdwn", "text": "Sent from SqlVitals on DBA-PC01" } ] }
              ]
            }
            """, ChatFormatter.Slack(Raised(new RaisedAlert(Cpu()))));
    }

    [Fact]
    public void OneAlert_ForTeams_HasAColouredTitleAFactSet_AndTheStartInEachReadersTimeZone()
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
                    { "type": "TextBlock", "text": "🔴 Critical: CPU on Sales-Prod", "weight": "Bolder", "size": "Medium", "color": "Attention", "wrap": true },
                    { "type": "FactSet", "facts": [ { "title": "Figure", "value": "97 % (threshold ≥ 90 %)" } ] },
                    { "type": "TextBlock", "text": "**Started** {{TeamsStarted}}", "wrap": true },
                    { "type": "TextBlock", "text": "Sent from SqlVitals on DBA-PC01", "isSubtle": true, "size": "Small", "wrap": true }
                  ]
                }
              }]
            }
            """, ChatFormatter.Teams(Raised(new RaisedAlert(Cpu()))));
    }

    [Fact]
    public void SeveralAlerts_ForSlack_AreLinesInOneSection()
    {
        AssertJson($$"""
            {
              "text": "🔴 2 alerts on Sales-Prod (1 critical): CPU and Blocking.",
              "blocks": [
                { "type": "header", "text": { "type": "plain_text", "text": "🔴 2 alerts on Sales-Prod (1 critical)", "emoji": true } },
                { "type": "section", "text": { "type": "mrkdwn", "text": "🔴 *CPU* 97 % (threshold ≥ 90 %) · since {{SlackStarted}}\n🟠 *Blocking* 3.5 min (threshold ≥ 1 min) · Blocking 3.5 min (4 blocked) · since <!date^1790764740^{date_short_pretty} {time}|2026-09-30 10:39 UTC>" } },
                { "type": "context", "elements": [ { "type": "mrkdwn", "text": "Sent from SqlVitals on DBA-PC01" } ] }
              ]
            }
            """, ChatFormatter.Slack(Raised(new RaisedAlert(Blocking()), new RaisedAlert(Cpu()))));
    }

    [Fact]
    public void SeveralAlerts_ForTeams_AreATextBlockEach_WithTheirStarts()
    {
        var body = JsonNode.Parse(ChatFormatter.Teams(Raised(new RaisedAlert(Blocking()), new RaisedAlert(Cpu()))))!
                   ["attachments"]![0]!["content"]!["body"]!.AsArray();

        Assert.Equal("Attention", (string?)body[0]!["color"]);
        Assert.Equal($"🔴 **CPU** 97 % (threshold ≥ 90 %) · since {TeamsStarted}", (string?)body[1]!["text"]);
        Assert.StartsWith("🟠 **Blocking** 3.5 min (threshold ≥ 1 min) · Blocking 3.5 min (4 blocked) · since {{DATE(2026-09-30T10:39:00Z",
                          (string?)body[2]!["text"]);
        Assert.Equal("Sent from SqlVitals on DBA-PC01", (string?)body[3]!["text"]);
    }

    [Fact]
    public void MoreThanTen_EndsWithAndNMore_ForBothServices()
    {
        var message = Raised(Enumerable.Range(0, 12).Select(_ => new RaisedAlert(Cpu())).ToArray());

        var slack = (string?)JsonNode.Parse(ChatFormatter.Slack(message))!["blocks"]![1]!["text"]!["text"];
        Assert.EndsWith("\n…and 2 more", slack);
        Assert.Equal(11, slack!.Split('\n').Length);

        var body = JsonNode.Parse(ChatFormatter.Teams(message))!["attachments"]![0]!["content"]!["body"]!.AsArray();
        Assert.Equal("…and 2 more", (string?)body[11]!["text"]);
        Assert.True((bool?)body[11]!["isSubtle"]);
    }

    [Fact]
    public void AnEscalation_ForTeams_IsRed()
    {
        var card = JsonNode.Parse(ChatFormatter.Teams(Raised(new RaisedAlert(Cpu(), NowCritical: true))))!;

        var title = card["attachments"]![0]!["content"]!["body"]![0]!;
        Assert.Equal(("🔴 Now critical: CPU on Sales-Prod", "Attention"), ((string?)title["text"], (string?)title["color"]));
    }

    [Fact]
    public void AnAlertTimeWithoutAKind_IsTakenAsUtc()
    {
        Assert.Equal(ChatFormatter.SlackTime(Started), ChatFormatter.SlackTime(DateTime.SpecifyKind(Started, DateTimeKind.Unspecified)));
    }
}
