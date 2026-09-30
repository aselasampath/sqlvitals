using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The chat service over a fake sender and a fake clock: the test message (#120), posting alerts
/// (#121), and the Failing status and its notification (#122).
/// </summary>
public sealed class ChatNotificationServiceTests
{
    private const string SlackUrl = "https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa";
    private const string TeamsUrl = "https://prod-27.westeurope.logic.azure.com:443/workflows/0a1b/triggers/manual/paths/invoke?sig=AbCdQk3a";

    private sealed class FakeSender(Func<Uri, WebhookResult> answer) : IWebhookSender
    {
        public FakeSender(Func<WebhookResult> answer) : this(_ => answer()) { }

        public readonly ConcurrentQueue<(Uri Url, string Json, ChatService Service)> Sent = new();

        public Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
        {
            Sent.Enqueue((url, json, service));
            return Task.FromResult(answer(url));
        }

        public IEnumerable<string> TitlesTo(string url) => Sent.Where(s => s.Url.OriginalString == url).Select(s => Title(s.Json, s.Service));

        private static string Title(string json, ChatService service) =>
            service == ChatService.Slack
                ? (string)JsonNode.Parse(json)!["blocks"]![0]!["text"]!["text"]!
                : (string)JsonNode.Parse(json)!["attachments"]![0]!["content"]!["body"]![0]!["text"]!;
    }

    private static readonly WebhookResult Ok       = new(WebhookOutcome.Posted, 200, null, TimeSpan.FromMilliseconds(120));
    private static readonly WebhookResult NotFound = new(WebhookOutcome.NotFound, 404, "no_service", TimeSpan.FromMilliseconds(80));
    private static readonly WebhookResult Busy     = new(WebhookOutcome.ServerError, 503, null, TimeSpan.FromMilliseconds(90));

    private static readonly ConnectionSettings Sales = new() { Name = "Sales-Prod", Server = "sql01" };
    private static readonly ConnectionSettings Hr    = new() { Name = "HR-DB", Server = "sql02" };

    private readonly ConcurrentQueue<string> _log = new();
    private readonly SteppingTime _time = new();

    private ChatNotificationService Create(FakeSender sender, params ChatChannel[] channels)
    {
        var service = new ChatNotificationService(sender, "DBA-PC01", _log.Enqueue, time: _time);
        service.Apply(Store(channels));
        return service;
    }

    private static ConnectionStore Store(params ChatChannel[] channels) => new()
    {
        Connections  = [Sales, Hr],
        ChatChannels = channels.ToList(),
    };

    private static ChatChannel Channel(string name = "DBA team", ChatService service = ChatService.Slack, string url = SlackUrl) =>
        new() { Name = name, Service = service, WebhookUrl = url };

    private static Alert Alert(Guid connection, HealthIndicator indicator = HealthIndicator.Cpu, HealthLevel severity = HealthLevel.Critical) =>
        new(Guid.NewGuid(), connection, indicator, severity, DateTime.UtcNow, null, 97, 90, "");

    private static IReadOnlyList<AlertChange> Started(params Alert[] alerts) =>
        alerts.Select(a => new AlertChange(a, AlertChangeKind.Started)).ToList();

    // ── Test message (#120) ───────────────────────────────────────────────────

    [Fact]
    public async Task ATestMessage_IsPostedInTheChannelsFormat_AndMarksItPosted()
    {
        var sender  = new FakeSender(() => Ok);
        var channel = Channel(service: ChatService.Teams, url: TeamsUrl);
        using var chat = Create(sender, channel);
        var changed = new List<Guid>();
        chat.StatusChanged += changed.Add;

        var result = await chat.SendTestAsync(channel.Id);

        Assert.True(result.IsSuccess);
        var (url, json, service) = Assert.Single(sender.Sent);
        Assert.Equal((TeamsUrl, ChatService.Teams), (url.OriginalString, service));
        Assert.Equal("message", (string?)JsonNode.Parse(json)!["type"]);
        Assert.Contains("Test message from SqlVitals on DBA-PC01. Alerts for all monitored connections will be posted here.", json);
        Assert.NotNull(chat.Status(channel.Id).LastPostedUtc);
        Assert.Equal([channel.Id], changed);
    }

    [Fact]
    public async Task TheTestMessage_NamesTheChosenConnections()
    {
        var channel = Channel();
        channel.AllConnections = false;
        channel.ConnectionIds  = [Sales.Id, Hr.Id];
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, channel);

        await chat.SendTestAsync(channel.Id);

        Assert.Contains("Alerts for HR-DB and Sales-Prod will be posted here.", Assert.Single(sender.Sent).Json);
    }

    [Fact]
    public async Task AFailedTest_LeavesTheStatusAlone()
    {
        var channel = Channel();
        using var chat = Create(new FakeSender(() => NotFound), channel);

        var result = await chat.SendTestAsync(channel.Id);

        Assert.Equal(WebhookOutcome.NotFound, result.Outcome);
        Assert.Equal(ChannelStatus.NeverUsed, chat.Status(channel.Id));
    }

    [Fact]
    public async Task ATestMessage_WorksWhileTheChannelIsOff()
    {
        var channel = Channel();
        channel.Enabled = false;
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, channel);

        Assert.True((await chat.SendTestAsync(channel.Id)).IsSuccess);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task EveryPostAndFailure_IsLoggedByName_NeverWithTheUrl()
    {
        var good = Channel("Good one");
        var bad  = Channel("Bad one", url: SlackUrl + "X");
        using var chat = Create(new FakeSender(url => url.OriginalString == SlackUrl ? Ok : NotFound), good, bad);

        await chat.SendTestAsync(good.Id);
        await chat.SendTestAsync(bad.Id);

        Assert.Collection(_log,
            l => Assert.Equal("Chat \"Good one\" (Slack): test message posted (200) in 120 ms", l),
            l => Assert.Equal("Chat \"Bad one\" (Slack): test message webhook not found (404): no_service", l));
    }

    [Fact]
    public async Task ASenderThatThrows_IsReportedAsAFailure_NotAnException()
    {
        var channel = Channel();
        using var chat = Create(new FakeSender(() => throw new InvalidOperationException($"bug posting to {SlackUrl}")), channel);

        var result = await chat.SendTestAsync(channel.Id);
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("hooks.slack.com", result.Describe(ChatService.Slack));
        // The test message once; the alert, then its 3 retries (#122).
        Assert.Equal(5, _log.Count);
        Assert.All(_log, l => Assert.DoesNotContain("hooks.slack.com", l));
    }

    [Fact]
    public async Task AStoreThatCouldntBeRead_IsIgnored_SoTheChannelsStay()
    {
        var channel = Channel();
        using var chat = Create(new FakeSender(() => Ok), channel);
        await chat.SendTestAsync(channel.Id);

        chat.Apply(new ConnectionStore { ReadFailed = true });

        Assert.Single(chat.Channels);
        Assert.NotNull(chat.Status(channel.Id).LastPostedUtc);
    }

    [Fact]
    public async Task ARemovedChannel_IsForgotten_WithItsStatus()
    {
        var channel = Channel();
        using var chat = Create(new FakeSender(() => Ok), channel);
        await chat.SendTestAsync(channel.Id);

        chat.Apply(Store());

        Assert.Empty(chat.Channels);
        Assert.Equal(ChannelStatus.NeverUsed, chat.Status(channel.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendTestAsync(channel.Id));
    }

    // ── Posting alerts (#121) ─────────────────────────────────────────────────

    [Fact]
    public async Task AnAlertStarting_IsPostedToEveryChannelThatsOn_InItsOwnFormat()
    {
        var slack = Channel("Slack one");
        var teams = Channel("Teams one", ChatService.Teams, TeamsUrl);
        var off   = Channel("Off one", url: SlackUrl + "Off");
        off.Enabled = false;
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, slack, teams, off);

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["🔴 Critical: CPU on Sales-Prod"], sender.TitlesTo(SlackUrl));
        Assert.Equal(["🔴 Critical: CPU on Sales-Prod"], sender.TitlesTo(TeamsUrl));
        Assert.Empty(sender.TitlesTo(SlackUrl + "Off"));
        Assert.All(sender.Sent.Select(s => s.Json), json => Assert.Contains("Sent from SqlVitals on DBA-PC01", json));
    }

    [Fact]
    public async Task APostedAlert_MarksTheChannelPosted_AndIsLoggedByName()
    {
        var channel = Channel();
        using var chat = Create(new FakeSender(() => Ok), channel);

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(chat.Status(channel.Id).LastPostedUtc);
        Assert.Equal("Chat \"DBA team\" (Slack): alert on Sales-Prod posted (200) in 120 ms", Assert.Single(_log));
    }

    [Fact]
    public async Task EachChannel_GetsItsMessagesInOrder_AndAFailingChannelDoesntStopAnother()
    {
        var good = Channel("Good one");
        var bad  = Channel("Bad one", url: SlackUrl + "Bad");
        var sender = new FakeSender(url => url.OriginalString == SlackUrl ? Ok : NotFound);
        using var chat = Create(sender, good, bad);

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        chat.OnAlertsRaised(Hr.Id, Started(Alert(Hr.Id, severity: HealthLevel.Warning)));
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id, HealthIndicator.TempDbSpace)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var expected = new[] { "🔴 Critical: CPU on Sales-Prod", "🟠 Warning: CPU on HR-DB", "🔴 Critical: TempDB space used on Sales-Prod" };
        Assert.Equal(expected, sender.TitlesTo(SlackUrl));
        Assert.Equal(expected, sender.TitlesTo(SlackUrl + "Bad"));
        Assert.Equal(3, _log.Count(l => l.Contains("webhook not found")));
        Assert.True(chat.Status(bad.Id).IsFailing);
        Assert.False(chat.Status(good.Id).IsFailing);
    }

    [Fact]
    public async Task ChosenConnectionsAndCriticalOnly_AreApplied()
    {
        var channel = Channel();
        channel.AllConnections = false;
        channel.ConnectionIds  = [Sales.Id];
        channel.CriticalOnly   = true;
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, channel);

        chat.OnAlertsRaised(Hr.Id, Started(Alert(Hr.Id)));
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id, severity: HealthLevel.Warning)));
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id, HealthIndicator.LogSpace)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["🔴 Critical: Log space used on Sales-Prod"], sender.TitlesTo(SlackUrl));
    }

    [Fact]
    public async Task MutingAConnectionsWindowsNotifications_DoesntStopItsChannels()
    {
        var channel = Channel();
        var sender  = new FakeSender(() => Ok);
        using var chat = new ChatNotificationService(sender, "DBA-PC01", _log.Enqueue, time: _time);
        var store = Store(channel);
        store.NotificationsMuted.Add(Sales.Id);
        chat.Apply(store);

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(store.NotifiesFor(Sales.Id));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task AChangeApplies_FromTheNextAlert_WithNoRestart()
    {
        var channel = Channel();
        var sender  = new FakeSender(() => Ok);
        using var chat = Create(sender, channel);

        var edited = channel.Clone();
        edited.Service      = ChatService.Teams;
        edited.WebhookUrl   = TeamsUrl;
        edited.CriticalOnly = true;
        chat.Apply(Store(edited));
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id, severity: HealthLevel.Warning)));
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id, HealthIndicator.LogSpace)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var (url, _, service) = Assert.Single(sender.Sent);
        Assert.Equal((TeamsUrl, ChatService.Teams), (url.OriginalString, service));
    }

    [Fact]
    public async Task TurningAChannelOff_ForgetsWhatItWasTold()
    {
        var channel = Channel();
        var sender  = new FakeSender(() => Ok);
        using var chat = Create(sender, channel);
        var warning = Alert(Sales.Id, severity: HealthLevel.Warning);
        chat.OnAlertsRaised(Sales.Id, Started(warning));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var off = channel.Clone();
        off.Enabled = false;
        chat.Apply(Store(off));
        chat.Apply(Store(channel));
        chat.OnAlertsRaised(Sales.Id, [new AlertChange(warning with { Severity = HealthLevel.Critical }, AlertChangeKind.Escalated)]);
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Not "Now critical": the channel doesn't remember the warning it was told before it went off.
        Assert.Equal("🔴 Critical: CPU on Sales-Prod", sender.TitlesTo(SlackUrl).Last());
    }

    [Fact]
    public async Task AnAlertOfAConnectionNoLongerSaved_IsPostedUnderADeletedConnection()
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());

        var gone = Guid.NewGuid();
        chat.OnAlertsRaised(gone, Started(Alert(gone)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["🔴 Critical: CPU on a deleted connection"], sender.TitlesTo(SlackUrl));
    }

    // ── Failing (#122) ────────────────────────────────────────────────────────

    private (ChatNotificationService Chat, ChatChannel Channel, List<string> Notices) Failing(Func<WebhookResult> answer)
    {
        var channel = Channel();
        var chat    = Create(new FakeSender(answer), channel);
        var notices = new List<string>();
        chat.StartedFailing += (_, text) => { lock (notices) notices.Add(text); };
        return (chat, channel, notices);
    }

    [Fact]
    public async Task AMessageGivenUp_MarksTheChannelFailing_AndNotifiesOnce()
    {
        var (chat, channel, notices) = Failing(() => NotFound);
        using var _ = chat;

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        chat.OnAlertsRaised(Hr.Id, Started(Alert(Hr.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var status = chat.Status(channel.Id);
        Assert.True(status.IsFailing);
        Assert.Equal("webhook not found (404): no_service", status.Problem);
        Assert.Equal(["Alerts aren't reaching DBA team (Slack): webhook not found. Check Settings → Alerts."], notices);
    }

    [Fact]
    public async Task FailingSince_IsWhenTheFirstMessageWasGivenUp_AfterItsRetries()
    {
        var (chat, channel, notices) = Failing(() => Busy);
        using var _ = chat;

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Tried at 0, 2, 12 and 42 s.
        Assert.Equal(new DateTime(2026, 9, 30, 10, 42, 42, DateTimeKind.Utc), chat.Status(channel.Id).FailingSinceUtc);
        Assert.Single(notices);
        Assert.Contains(_log, l => l.EndsWith("alert on Sales-Prod server error (503); retry 1 of 3 in 2 s"));
        Assert.Contains(_log, l => l.EndsWith("alert on Sales-Prod server error (503); not sent after 4 tries"));
    }

    [Fact]
    public async Task AChannelThatComesBack_RecoversByItself_AndNotifiesAgainIfItFailsAgain()
    {
        var answer = NotFound;
        var channel = Channel();
        using var chat = Create(new FakeSender(() => Volatile.Read(ref answer)), channel);
        var notices = 0;
        chat.StartedFailing += (_, _) => Interlocked.Increment(ref notices);

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Write(ref answer, Ok);
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id, HealthIndicator.Blocking)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(chat.Status(channel.Id).IsFailing);
        Assert.NotNull(chat.Status(channel.Id).LastPostedUtc);

        Volatile.Write(ref answer, NotFound);
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id, HealthIndicator.LogSpace)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(chat.Status(channel.Id).IsFailing);
        Assert.Equal(2, notices);
    }

    [Fact]
    public async Task ATestMessageThatWorks_ClearsFailing_AndOneThatFails_DoesntMarkIt()
    {
        var answer = NotFound;
        var channel = Channel();
        using var chat = Create(new FakeSender(() => Volatile.Read(ref answer)), channel);
        var notices = 0;
        chat.StartedFailing += (_, _) => Interlocked.Increment(ref notices);

        await chat.SendTestAsync(channel.Id);
        Assert.Equal(ChannelStatus.NeverUsed, chat.Status(channel.Id));

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(chat.Status(channel.Id).IsFailing);

        Volatile.Write(ref answer, Ok);
        await chat.SendTestAsync(channel.Id);

        Assert.False(chat.Status(channel.Id).IsFailing);
        Assert.Equal(1, notices);
    }

    [Fact]
    public async Task TurningAFailingChannelOff_ClearsFailing()
    {
        var (chat, channel, _) = Failing(() => NotFound);
        using var __ = chat;
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var off = channel.Clone();
        off.Enabled = false;
        chat.Apply(Store(off));

        Assert.False(chat.Status(channel.Id).IsFailing);
    }

    [Fact]
    public async Task AFailingChannel_NotifiesEvenForAMutedConnection()
    {
        var (chat, _, notices) = Failing(() => NotFound);
        using var __ = chat;
        var store = Store(chat.Channels.ToArray());
        store.NotificationsMuted.Add(Sales.Id);
        chat.Apply(store);

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(notices);
    }

    [Fact]
    public async Task TheUrl_IsNeverLogged_OnTheRetryAndFailurePaths()
    {
        var tries = 0;
        var channel = Channel();
        using var chat = Create(new FakeSender(_ => Interlocked.Increment(ref tries) switch
        {
            1 => Busy,
            2 => throw new System.Net.Http.HttpRequestException($"POST {SlackUrl} failed"),
            3 => new WebhookResult(WebhookOutcome.RateLimited, 429, null, TimeSpan.Zero, TimeSpan.FromSeconds(5)),
            4 => new WebhookResult(WebhookOutcome.TimedOut, null, null, TimeSpan.Zero),
            _ => NotFound,
        }), channel);
        var notices = new ConcurrentQueue<string>();
        chat.StartedFailing += (_, text) => notices.Enqueue(text);

        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(5, _log.Count);
        Assert.All(_log.Concat(notices).Append(chat.Status(channel.Id).Text(DateTime.Now)), text =>
        {
            Assert.DoesNotContain("hooks.slack.com", text);
            Assert.DoesNotContain("T0001", text);
        });
    }

    [Fact]
    public async Task GettingWorse_PostsNothing()
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());

        chat.OnAlertsRaised(Sales.Id, [new AlertChange(Alert(Sales.Id), AlertChangeKind.Worsened)]);
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(sender.Sent);
    }
}
