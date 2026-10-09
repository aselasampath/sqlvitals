using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>
/// The heartbeat (#170): its schedule in the PC's time zone, across daylight saving changes; the
/// message; the ping URL's checks; and the setup steps the channel form copies.
/// </summary>
public sealed class HeartbeatTests
{
    // Clocks go forward on 29 March 2026 at 02:00 and back on 25 October 2026 at 03:00.
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, Berlin.GetUtcOffset(new DateTime(2026, month, day, hour, minute, 0)));

    // ── Schedule ──────────────────────────────────────────────────────────────

    [Fact]
    public void Daily_IsTodayAtTheTime_OrTomorrowWhenItsPast()
    {
        var daily = HeartbeatSchedule.DailyAt(TimeSpan.FromHours(9));

        Assert.Equal(Local(10, 10, 9), daily.Next(Local(10, 10, 8, 59), Berlin));
        Assert.Equal(Local(10, 11, 9), daily.Next(Local(10, 10, 9), Berlin)); // strictly after
        Assert.Equal(Local(10, 11, 9), daily.Next(Local(10, 10, 17), Berlin));
    }

    [Fact]
    public void EveryNHours_CountsFromMidnight_WheneverItStarted()
    {
        var six = HeartbeatSchedule.EveryHours(6);

        Assert.Equal(Local(10, 10, 12), six.Next(Local(10, 10, 7, 13), Berlin));
        Assert.Equal(Local(10, 11, 0), six.Next(Local(10, 10, 18), Berlin));
        Assert.Equal(Local(10, 10, 8), HeartbeatSchedule.EveryHours(1).Next(Local(10, 10, 7, 59), Berlin));
    }

    [Fact]
    public void ATimeSkippedByClocksGoingForward_IsSentAnHourLater()
    {
        var at0230 = HeartbeatSchedule.DailyAt(new TimeSpan(2, 30, 0));

        var next = at0230.Next(Local(3, 29, 1), Berlin)!.Value;

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)), next);
    }

    [Fact]
    public void ATimeThatHappensTwice_WhenClocksGoBack_IsSentOnce()
    {
        var at0230 = HeartbeatSchedule.DailyAt(new TimeSpan(2, 30, 0));
        var first = at0230.Next(Local(10, 25, 1), Berlin)!.Value;

        var after = at0230.Next(first, Berlin)!.Value;

        Assert.Equal(new DateTime(2026, 10, 25, 2, 30, 0), first.DateTime);
        Assert.Equal(new DateTime(2026, 10, 26, 2, 30, 0), after.DateTime);
    }

    [Fact]
    public void EveryHour_OnTheDayClocksGoBack_KeepsComingInOrder()
    {
        var hourly = HeartbeatSchedule.EveryHours(1);
        var times = new List<DateTimeOffset>();
        var at = Local(10, 25, 0, 30);
        for (var i = 0; i < 5; i++)
            times.Add(at = hourly.Next(at, Berlin)!.Value);

        Assert.Equal(times.OrderBy(t => t), times);
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(HeartbeatEvery.Off,   6, 9, false)]
    [InlineData(HeartbeatEvery.Hours, 6, 9, true)]
    [InlineData(HeartbeatEvery.Hours, 5, 9, false)]  // doesn't divide a day
    [InlineData(HeartbeatEvery.Hours, 0, 9, false)]
    [InlineData(HeartbeatEvery.Daily, 0, 9, true)]
    [InlineData(HeartbeatEvery.Daily, 0, 24, false)]
    public void OnlyASchedulesThatCanBeKept_IsOn(HeartbeatEvery every, int hours, int atHour, bool on)
    {
        var schedule = new HeartbeatSchedule(every, hours, TimeSpan.FromHours(atHour));

        Assert.Equal(on, schedule.IsOn);
        Assert.Equal(on, schedule.Next(Local(10, 10, 8), Berlin) is not null);
    }

    [Theory]
    [InlineData(HeartbeatEvery.Off,   0,  "Off")]
    [InlineData(HeartbeatEvery.Hours, 1,  "Every hour")]
    [InlineData(HeartbeatEvery.Hours, 6,  "Every 6 hours (00:00, 06:00, 12:00, 18:00)")]
    [InlineData(HeartbeatEvery.Hours, 3,  "Every 3 hours, from 00:00")]
    [InlineData(HeartbeatEvery.Daily, 0,  "Daily at 09:30")]
    public void Describe_SaysWhen(HeartbeatEvery every, int hours, string expected)
    {
        Assert.Equal(expected, new HeartbeatSchedule(every, hours, new TimeSpan(9, 30, 0)).Describe());
    }

    // ── Message ───────────────────────────────────────────────────────────────

    private static readonly DateTime Next = new(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheHeartbeat_SaysWhatsWatched_AndWhenTheNextIsDue()
    {
        var message = ChatMessageBuilder.Heartbeat(["Sales-Prod", "HR-DB"], "DBA-PC01", Next, HeartbeatSchedule.DailyAt(TimeSpan.FromHours(9)));

        Assert.Equal("💓 SqlVitals heartbeat: watching 2 servers", message.Title);
        Assert.StartsWith(ChatMessageBuilder.HeartbeatTitle, message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 is running and watching HR-DB and Sales-Prod.", message.Text);
        Assert.Equal([("Next heartbeat", "", (DateTime?)Next), ("Schedule", "Daily at 09:00", null)],
                     message.Facts.Select(f => (f.Title, f.Value, f.TimeUtc)));
        Assert.Equal(ChatTone.Neutral, message.Tone);
        Assert.False(message.CanFold);
    }

    [Fact]
    public void AHeartbeatWatchingNothing_IsAWarning()
    {
        var message = ChatMessageBuilder.Heartbeat([], "DBA-PC01", Next, HeartbeatSchedule.EveryHours(6));

        Assert.Equal("💓 SqlVitals heartbeat: watching no servers", message.Title);
        Assert.Equal(ChatTone.Warning, message.Tone);
        Assert.Contains("isn't watching any server this channel takes, so no alerts will be posted", message.Text);
    }

    [Fact]
    public void AHeartbeatForOneServer_NamesIt_AndManyAreCut()
    {
        Assert.Equal("💓 SqlVitals heartbeat: watching Sales-Prod",
                     ChatMessageBuilder.Heartbeat(["Sales-Prod"], "DBA-PC01", Next, HeartbeatSchedule.EveryHours(1)).Title);

        var many = Enumerable.Range(1, 12).Select(i => $"SQL{i:00}").ToList();
        Assert.EndsWith("SQL10 and 2 more.", ChatMessageBuilder.Heartbeat(many, "DBA-PC01", Next, HeartbeatSchedule.EveryHours(1)).Text);
    }

    [Fact]
    public void TheHeartbeat_ForTeams_ShowsTheNextOneInTheReadersTimeZone()
    {
        var json = ChatFormatter.Teams(ChatMessageBuilder.Heartbeat(["Sales-Prod"], "DBA-PC01", Next, HeartbeatSchedule.EveryHours(6)));

        Assert.Contains("**Next heartbeat** {{DATE(2026-10-11T07:00:00Z, SHORT)}} {{TIME(2026-10-11T07:00:00Z)}}", json);
        // The emoji is escaped in the JSON; a check looks for the words.
        Assert.Contains("SqlVitals heartbeat: watching Sales-Prod", json);
    }

    // ── Ping URL ──────────────────────────────────────────────────────────────

    private const string Webhook = "https://hooks.slack.com/services/T0001/B0002/abcdEFGH";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("https://hc-ping.com/0f3b8a5e-1c2d-4e5f-8a9b-0c1d2e3f4a5b")]
    [InlineData("https://uptime.betterstack.com/api/v1/heartbeat/AbCd1234")]
    public void APingUrl_IsOptional_AndAnyHttpsUrlWillDo(string? url)
    {
        Assert.Null(HeartbeatPing.Check(url, Webhook));
    }

    [Theory]
    [InlineData("http://hc-ping.com/abc", "must start with https://")]
    [InlineData("hc-ping.com/abc", "isn't a whole URL")]
    [InlineData("https://hooks.slack.com/services/T0001/B0002/abcdEFGH/", "is the channel's webhook")]
    public void ABadPingUrl_SaysWhy(string url, string why)
    {
        Assert.Contains(why, HeartbeatPing.Check(url, Webhook));
    }

    [Theory]
    [InlineData(200, "text/plain", WebhookOutcome.Posted)]
    [InlineData(202, null,         WebhookOutcome.Posted)]
    [InlineData(200, "text/html",  WebhookOutcome.Unexpected)] // a proxy's or a Wi-Fi sign-in page
    [InlineData(404, "text/plain", WebhookOutcome.NotFound)]
    [InlineData(503, null,         WebhookOutcome.ServerError)]
    public void APing_SucceedsOnAny2xxThatIsntAPage(int status, string? mediaType, WebhookOutcome outcome)
    {
        Assert.Equal(outcome, WebhookResult.FromPing(status, mediaType, TimeSpan.Zero).Outcome);
    }

    // ── Setup steps ───────────────────────────────────────────────────────────

    [Fact]
    public void SlackSteps_AreADeadMansSwitch_WithNoTeamsFlow()
    {
        var steps = HeartbeatSetup.Steps(ChatService.Slack, HeartbeatSchedule.DailyAt(TimeSpan.FromHours(9)), "DBA-PC01");

        Assert.Contains("Slack can't alert when a message doesn't arrive", steps);
        Assert.Contains("Expect a ping every day, with a grace time of 30 minutes.", steps);
        Assert.Contains("Connect the service to Slack", steps);
        Assert.DoesNotContain("Power Automate", steps);
    }

    [Fact]
    public void TeamsSteps_AddAScheduledFlow_ThatLooksHalfAnHourAfterEachHeartbeat()
    {
        var steps = HeartbeatSetup.Steps(ChatService.Teams, HeartbeatSchedule.EveryHours(6), "DBA-PC01");

        Assert.Contains("Recurrence: every 6 hours, starting 00:30, in DBA-PC01's time zone.", steps);
        Assert.Contains("contains(string(item()?['attachments']), 'SqlVitals heartbeat')", steps);
        Assert.Contains("addHours(utcNow(), -6)", steps);
        Assert.Contains("Connect the service to Microsoft Teams", steps);
    }

    [Fact]
    public void TeamsSteps_ForADailyHeartbeat_StartAtItsTimePlusTheGrace()
    {
        var steps = HeartbeatSetup.Steps(ChatService.Teams, HeartbeatSchedule.DailyAt(new TimeSpan(23, 45, 0)), "DBA-PC01");

        Assert.Contains("Recurrence: every day, starting 00:15", steps);
        Assert.Contains("addHours(utcNow(), -24)", steps);
    }

    [Fact]
    public void WithNoHeartbeat_TheStepsSayToChooseOne()
    {
        Assert.StartsWith("Choose a heartbeat schedule first", HeartbeatSetup.Steps(ChatService.Teams, HeartbeatSchedule.Off, "DBA-PC01"));
    }
}
