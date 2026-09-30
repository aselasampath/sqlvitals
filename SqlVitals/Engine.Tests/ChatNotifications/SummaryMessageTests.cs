using System.Globalization;
using System.Text.Json.Nodes;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>The one message that replaces those that piled up while a channel was busy (#122).</summary>
public sealed class SummaryMessageTests
{
    private static ChatMessage Raised(string connection, params (HealthIndicator Indicator, HealthLevel Severity)[] alerts)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return ChatMessageBuilder.Raised(connection, alerts.Select(a => new RaisedAlert(
                new Alert(Guid.NewGuid(), Guid.NewGuid(), a.Indicator, a.Severity, AlertMessageTests.Started, null, 97, 90, ""))).ToList(),
                "DBA-PC01");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ASummary_CountsTheMessages_AndListsEachConnectionsIndicators_CriticalFirst()
    {
        var summary = ChatMessageBuilder.Summary([
            Raised("HR-DB", (HealthIndicator.Cpu, HealthLevel.Warning)),
            Raised("Sales-Prod", (HealthIndicator.Blocking, HealthLevel.Warning)),
            Raised("Sales-Prod", (HealthIndicator.LogSpace, HealthLevel.Critical), (HealthIndicator.Blocking, HealthLevel.Warning)),
        ]);

        Assert.Equal("📋 3 more messages combined", summary.Title);
        Assert.Equal("3 more messages were combined while the channel was busy. See the Alerts page in SqlVitals for the details.", summary.Text);
        Assert.Equal(ChatTone.Critical, summary.Tone);
        Assert.Equal(
            [new ChatLine(ChatTone.Critical, "Sales-Prod", "new or critical: Blocking and Log space used"),
             new ChatLine(ChatTone.Warning, "HR-DB", "new or critical: CPU")],
            summary.Lines);
        Assert.Equal("Sent from SqlVitals on DBA-PC01", summary.SentFrom);
        Assert.Equal("3 more messages were combined while the channel was busy: alerts on Sales-Prod and HR-DB.", summary.FallbackText);
        Assert.Equal("summary of 3 messages", summary.About);
    }

    [Fact]
    public void ASummaryOfOne_SaysMessage()
    {
        var summary = ChatMessageBuilder.Summary([Raised("HR-DB", (HealthIndicator.Cpu, HealthLevel.Warning))]);

        Assert.Equal("📋 1 more message combined", summary.Title);
        Assert.Equal(ChatTone.Warning, summary.Tone);
        Assert.Equal("summary of 1 message", summary.About);
    }

    [Fact]
    public void ManyConnections_AreCutAfter10Lines()
    {
        var summary = ChatMessageBuilder.Summary(
            Enumerable.Range(1, 13).Select(i => Raised($"SQL{i:00}", (HealthIndicator.Cpu, HealthLevel.Warning))).ToList());

        Assert.Equal(10, summary.Lines.Count);
        Assert.Equal(3, summary.More);
    }

    [Fact]
    public void ASummary_IsWrittenForBothServices()
    {
        var summary = ChatMessageBuilder.Summary([
            Raised("Sales<prod>", (HealthIndicator.Cpu, HealthLevel.Critical)),
            Raised("HR-DB", (HealthIndicator.Cpu, HealthLevel.Warning)),
        ]);

        var slack = ChatFormatter.Format(summary, ChatService.Slack);
        var teams = ChatFormatter.Format(summary, ChatService.Teams);

        Assert.Equal("📋 2 more messages combined", (string?)JsonNode.Parse(slack)!["blocks"]![0]!["text"]!["text"]);
        var slackTexts = JsonNode.Parse(slack)!["blocks"]!.AsArray().Select(b => (string?)b!["text"]?["text"]);
        Assert.Contains("🔴 *Sales&lt;prod&gt;* new or critical: CPU\n🟠 *HR-DB* new or critical: CPU", slackTexts);
        var teamsTexts = JsonNode.Parse(teams)!["attachments"]![0]!["content"]!["body"]!.AsArray().Select(b => (string?)b!["text"]).ToList();
        Assert.Contains("🔴 **Sales<prod>** new or critical: CPU", teamsTexts);
        Assert.Contains("Sent from SqlVitals on DBA-PC01", teamsTexts);
    }

    [Fact]
    public void NothingToSummarise_IsABug()
    {
        Assert.Throws<ArgumentException>(() => ChatMessageBuilder.Summary([]));
    }
}
