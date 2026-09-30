using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

public sealed class ChannelStatusTests
{
    [Fact]
    public void ANewChannel_IsNeverUsed()
    {
        Assert.Equal("Never used", ChannelStatus.NeverUsed.Text(DateTime.Now));
    }

    [Fact]
    public void APostToday_ShowsTheTime()
    {
        var now = new DateTime(2026, 9, 30, 14, 0, 0, DateTimeKind.Local);
        var posted = new DateTime(2026, 9, 30, 10, 42, 0, DateTimeKind.Local).ToUniversalTime();

        Assert.Equal("Last posted 10:42", new ChannelStatus(posted).Text(now));
    }

    [Fact]
    public void APostOnAnotherDay_ShowsTheDateToo()
    {
        var now = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Local);
        var posted = new DateTime(2026, 9, 30, 10, 42, 0, DateTimeKind.Local).ToUniversalTime();

        var text = new ChannelStatus(posted).Text(now);

        Assert.StartsWith("Last posted 30 ", text);
        Assert.EndsWith(" 10:42", text);
    }

    // ── Failing (#122) ────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 9, 30, 14, 0, 0, DateTimeKind.Local);

    private static DateTime At(int hour, int minute) => new DateTime(2026, 9, 30, hour, minute, 0, DateTimeKind.Local).ToUniversalTime();

    [Fact]
    public void AGivenUpMessage_ShowsFailingSince_AndWhy()
    {
        var status = ChannelStatus.NeverUsed.Failed(At(10, 42), "webhook not found (404)");

        Assert.True(status.IsFailing);
        Assert.Equal("⚠ Failing since 10:42: webhook not found (404)", status.Text(Now));
    }

    [Fact]
    public void Failing_KeepsTheFirstFailuresTime_AndTheLatestReason()
    {
        var status = new ChannelStatus(At(9, 0))
            .Failed(At(10, 42), "server error (503)")
            .Failed(At(11, 5), "webhook not found (404)");

        Assert.Equal("⚠ Failing since 10:42: webhook not found (404)", status.Text(Now));
        Assert.Equal(At(9, 0), status.LastPostedUtc);
    }

    [Fact]
    public void APost_ClearsFailing()
    {
        var status = ChannelStatus.NeverUsed.Failed(At(10, 42), "server error (503)").Posted(At(11, 0));

        Assert.False(status.IsFailing);
        Assert.Equal("Last posted 11:00", status.Text(Now));
    }

    [Fact]
    public void TheNotification_NamesTheChannelAndService_AndWhatsWrong()
    {
        var notFound = new WebhookResult(WebhookOutcome.NotFound, 404, "no_service", TimeSpan.Zero);

        Assert.Equal("Alerts aren't reaching DBA team (Microsoft Teams): webhook not found. Check Settings → Alerts.",
                     ChannelStatus.FailingNotice("DBA team", ChatService.Teams, notFound));
        // Not "DBA team (Teams) (Microsoft Teams)".
        Assert.Equal("Alerts aren't reaching DBA team (Teams): webhook not found. Check Settings → Alerts.",
                     ChannelStatus.FailingNotice("DBA team (Teams)", ChatService.Teams, notFound));
    }

    [Fact]
    public void TheNotification_FitsWindowsLimit_WithALongName()
    {
        var text = ChannelStatus.FailingNotice(new string('x', 300), ChatService.Slack,
                                               new WebhookResult(WebhookOutcome.Unexpected, 200, null, TimeSpan.Zero));

        Assert.True(text.Length <= 255, text);
        Assert.True(ChannelStatus.FailingTitle.Length <= 63);
    }

    [Theory]
    [InlineData(WebhookOutcome.NotFound, "webhook not found")]
    [InlineData(WebhookOutcome.Rejected, "rejected by Slack")]
    [InlineData(WebhookOutcome.RateLimited, "Slack says too many messages")]
    [InlineData(WebhookOutcome.ServerError, "Slack has a server error")]
    [InlineData(WebhookOutcome.Unreachable, "can't reach Slack")]
    [InlineData(WebhookOutcome.TimedOut, "Slack didn't answer in time")]
    public void TheProblem_IsSaidInAFewWords(WebhookOutcome outcome, string expected)
    {
        Assert.Equal(expected, new WebhookResult(outcome, null, "invalid_token", TimeSpan.Zero).Problem(ChatService.Slack));
    }
}
