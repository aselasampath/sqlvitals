using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The chat service over a fake sender and a fake clock: the test message (#120), posting alerts
/// (#121), the Failing status and its notification (#122), ends and Exit (#123), and stopped
/// watching and watching again (#170).
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

        public readonly ConcurrentQueue<Uri> Pinged = new();

        public Task<WebhookResult> PingAsync(Uri url, CancellationToken cancel = default)
        {
            Pinged.Enqueue(url);
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
        Assert.Equal(["Alerts aren't reaching DBA team (Slack): webhook not found. Check Settings → Alerts & chat."], notices);
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
        Assert.Contains(_log, l => l.EndsWith("alert on Sales-Prod server error (503); retry 1 of 3 in 2 s", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.EndsWith("alert on Sales-Prod server error (503); not sent after 4 tries", StringComparison.Ordinal));
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

    // ── Ends and Exit (#123) ──────────────────────────────────────────────────

    private static AlertChange Ended(Alert alert, AlertEndReason reason) =>
        new(alert with { EndedUtc = alert.StartedUtc.AddMinutes(12), EndReason = reason }, AlertChangeKind.Ended);

    [Fact]
    public async Task BackToNormal_IsPosted_OnlyToChannelsThatWereTold_AndHavePostEndsOn()
    {
        var told    = Channel("Told");
        var noEnds  = Channel("No ends", url: SlackUrl + "N");
        noEnds.PostEnds = false;
        var sender  = new FakeSender(() => Ok);
        using var chat = Create(sender, told, noEnds);
        var alert = Alert(Sales.Id);
        chat.OnAlertsRaised(Sales.Id, Started(alert));

        var later = Channel("Added later", url: SlackUrl + "L");
        chat.Apply(Store(told, noEnds, later));
        chat.OnAlertsRaised(Sales.Id, [Ended(alert, AlertEndReason.Recovered)]);
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["🔴 Critical: CPU on Sales-Prod", "✅ Back to normal: CPU on Sales-Prod"], sender.TitlesTo(SlackUrl));
        Assert.Equal(["🔴 Critical: CPU on Sales-Prod"], sender.TitlesTo(SlackUrl + "N"));
        Assert.Empty(sender.TitlesTo(SlackUrl + "L"));
    }

    // What MonitoringManager.WatchingChanged says.
    private static WatchChange Start(params ConnectionSettings[] connections) =>
        new(connections.Select(c => new WatchedConnection(c.Id, c.DisplayName)).ToList(), [], []);

    private static WatchChange Stop(WatchStop why, params ConnectionSettings[] connections) =>
        new([], connections.Select(c => new StoppedWatching(c.Id, c.DisplayName, why)).ToList(), []);

    private static AlertChange StoppedEnd(Alert alert) => Ended(alert, AlertEndReason.MonitoringStopped);

    [Fact]
    public async Task StoppingAConnection_PostsOneStoppedWatchingMessage_WithTheAlertsStillActive_WhetherOrNotEndsAreOn()
    {
        var channel = Channel();
        channel.PostEnds = false;
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, channel);
        chat.OnWatchingChanged(Start(Sales));
        var (cpu, log) = (Alert(Sales.Id), Alert(Sales.Id, HealthIndicator.LogSpace));
        chat.OnAlertsRaised(Sales.Id, Started(cpu, log));

        // As MonitoringManager does it: the session's ends, then what stopped.
        chat.OnAlertsRaised(Sales.Id, [StoppedEnd(cpu), StoppedEnd(log)]);
        chat.OnWatchingChanged(Stop(WatchStop.StoppedByUser, Sales));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["▶ SqlVitals is watching Sales-Prod", "🔴 2 alerts on Sales-Prod (2 critical)", "⏹ SqlVitals stopped watching Sales-Prod"],
                     sender.TitlesTo(SlackUrl));
        Assert.Contains("SqlVitals on DBA-PC01 stopped watching Sales-Prod because monitoring was stopped in SqlVitals.", sender.Sent.Last().Json);
        Assert.Contains("Sales-Prod had 2 alerts still active: CPU, Log space used.", sender.Sent.Last().Json);
    }

    [Fact]
    public async Task StoppedAndWatchingAgain_GoOnlyToChannelsThatTakeTheConnection_WithNothingTold()
    {
        var all   = Channel("All");
        var hr    = Channel("HR only", url: SlackUrl + "H");
        hr.AllConnections = false;
        hr.ConnectionIds  = [Hr.Id];
        var off   = Channel("Off", url: SlackUrl + "O");
        off.Enabled = false;
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, all, hr, off);

        chat.OnWatchingChanged(Start(Sales));
        chat.OnWatchingChanged(Stop(WatchStop.AutomaticOff, Sales));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["▶ SqlVitals is watching Sales-Prod", "⏹ SqlVitals stopped watching Sales-Prod"], sender.TitlesTo(SlackUrl));
        Assert.Empty(sender.TitlesTo(SlackUrl + "H"));
        Assert.Empty(sender.TitlesTo(SlackUrl + "O"));
    }

    [Fact]
    public async Task AChannelToldAboutAnAlert_HearsTheStop_EvenAfterItsFiltersChanged()
    {
        var channel = Channel();
        var sender  = new FakeSender(() => Ok);
        using var chat = Create(sender, channel);
        var alert = Alert(Sales.Id);
        chat.OnAlertsRaised(Sales.Id, Started(alert));

        var narrowed = Channel();
        narrowed.Id = channel.Id;
        narrowed.AllConnections = false;
        narrowed.ConnectionIds  = [Hr.Id];
        chat.Apply(Store(narrowed));
        chat.OnAlertsRaised(Sales.Id, [StoppedEnd(alert)]);
        chat.OnWatchingChanged(Stop(WatchStop.StoppedByUser, Sales));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("⏹ SqlVitals stopped watching Sales-Prod", sender.TitlesTo(SlackUrl).Last());
    }

    [Fact]
    public async Task ARemovedConnection_IsNamedInItsStoppedMessage()
    {
        var channel = Channel();
        var sender  = new FakeSender(() => Ok);
        using var chat = Create(sender, channel);
        chat.OnWatchingChanged(Start(Sales));
        var alert = Alert(Sales.Id);
        chat.OnAlertsRaised(Sales.Id, Started(alert));

        // Removing a connection saves the settings (forgetting its name) before its session stops (F8).
        chat.Apply(new ConnectionStore { Connections = [Hr], ChatChannels = [channel] });
        chat.OnAlertsRaised(Sales.Id, [StoppedEnd(alert)]);
        chat.OnWatchingChanged(Stop(WatchStop.ConnectionRemoved, Sales));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("⏹ SqlVitals stopped watching Sales-Prod", sender.TitlesTo(SlackUrl).Last());
        Assert.Contains("because the connection was removed from SqlVitals.", sender.Sent.Last().Json);
    }

    [Fact]
    public async Task AConnectionRestartedWithNewDetails_PostsNothing()
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());
        chat.OnWatchingChanged(Start(Sales));
        var alert = Alert(Sales.Id);
        chat.OnAlertsRaised(Sales.Id, Started(alert));

        chat.OnAlertsRaised(Sales.Id, [StoppedEnd(alert)]);
        chat.OnWatchingChanged(new WatchChange([], [], [Sales.Id]));
        // Its alerts gone, a later stop lists none.
        chat.OnWatchingChanged(Stop(WatchStop.StoppedByUser, Sales));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, sender.Sent.Count);
        Assert.DoesNotContain("still active", sender.Sent.Last().Json);
    }

    [Fact]
    public async Task WatchingAgain_SaysHowLongItWasntWatched()
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());

        var since = _time.GetUtcNow().UtcDateTime.AddHours(-3).AddMinutes(-12);
        chat.OnWatchingChanged(new WatchChange([new WatchedConnection(Sales.Id, "Sales-Prod", since)], [], []));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["▶ SqlVitals is watching Sales-Prod again"], sender.TitlesTo(SlackUrl));
        Assert.Contains("is watching Sales-Prod again, after 3 h 12 min without monitoring.", sender.Sent.Single().Json);
    }

    [Theory]
    [InlineData(WatchStop.AppExit,         "because the app was closed.")]
    [InlineData(WatchStop.WindowsShutdown, "because Windows is shutting down.")]
    [InlineData(WatchStop.WindowsSignOut,  "because the Windows user signed out.")]
    public void AtExit_EachChannelGetsOneStoppedMessage_ForEveryConnection_SayingWhy(WatchStop why, string because)
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());
        chat.OnWatchingChanged(Start(Sales, Hr));
        var (sales, hr) = (Alert(Sales.Id), Alert(Hr.Id, HealthIndicator.Blocking));
        chat.OnAlertsRaised(Sales.Id, Started(sales));
        chat.OnAlertsRaised(Hr.Id, Started(hr));

        chat.BeginExit(why);
        chat.OnAlertsRaised(Sales.Id, [StoppedEnd(sales)]);
        chat.OnAlertsRaised(Hr.Id, [StoppedEnd(hr)]);
        chat.OnAllStopped();
        chat.Shutdown(TimeSpan.FromSeconds(5));

        Assert.Equal("⏹ SqlVitals stopped watching 2 servers", sender.TitlesTo(SlackUrl).Last());
        Assert.Contains($"stopped watching Sales-Prod and HR-DB {because}", sender.Sent.Last().Json);
        Assert.Contains("*Sales-Prod* 1 alert still active: CPU", sender.Sent.Last().Json);
        Assert.Contains("*HR-DB* 1 alert still active: Blocking", sender.Sent.Last().Json);
        Assert.Equal(4, sender.Sent.Count);
    }

    // ── Sleep (#170) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GoingToSleep_SaysSo_AndWaking_SaysHowLongItSlept()
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());
        chat.OnWatchingChanged(Start(Sales));

        chat.OnGoingToSleep();
        _time.Advance(TimeSpan.FromHours(7).Add(TimeSpan.FromMinutes(5)));
        chat.OnWokeUp();
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["▶ SqlVitals is watching Sales-Prod", "⏹ SqlVitals stopped watching Sales-Prod", "▶ SqlVitals is watching Sales-Prod again"],
                     sender.TitlesTo(SlackUrl));
        Assert.Contains("because the PC is going to sleep.", sender.Sent.ElementAt(1).Json);
        Assert.Contains("again, after 7 h 05 min without monitoring.", sender.Sent.Last().Json);
    }

    [Fact]
    public async Task SleepingWithNothingWatched_SaysNothing_AndWakingWithoutSleeping_SaysNothing()
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());

        chat.OnWokeUp();
        chat.OnGoingToSleep();
        chat.OnWokeUp();
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void ExitingWhileAsleep_DoesntSayItStoppedTwice()
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());
        chat.OnWatchingChanged(Start(Sales));
        chat.OnGoingToSleep();

        chat.BeginExit(WatchStop.WindowsShutdown);
        chat.OnAllStopped();
        chat.Shutdown(TimeSpan.FromSeconds(5));

        Assert.Equal(["▶ SqlVitals is watching Sales-Prod", "⏹ SqlVitals stopped watching Sales-Prod"], sender.TitlesTo(SlackUrl));
        Assert.Contains("because the PC is going to sleep.", sender.Sent.Last().Json);
    }

    [Fact]
    public void AtExit_WhatCantBeSentInTime_IsDroppedAndLogged()
    {
        // Two channels stuck on a request that never answers.
        using var chat = new ChatNotificationService(new HangingSender(), "DBA-PC01", _log.Enqueue, time: _time);
        chat.Apply(Store(Channel(), Channel("Ops", url: SlackUrl + "O")));
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id)));
        chat.OnAlertsRaised(Hr.Id, Started(Alert(Hr.Id)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        chat.BeginExit();
        chat.Shutdown(TimeSpan.FromMilliseconds(300));
        chat.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"Exit took {watch.Elapsed}.");
        Assert.Contains("Chat \"DBA team\": 2 message(s) not sent, because SqlVitals exited", _log);
        Assert.Contains("Chat \"Ops\": 2 message(s) not sent, because SqlVitals exited", _log);
    }

    [Fact]
    public async Task AtExit_AChannelThatFails_DoesntNotifyOrChangeItsStatus()
    {
        var channel = Channel();
        using var chat = Create(new FakeSender(() => NotFound), channel);
        var notices = 0;
        chat.StartedFailing += (_, _) => Interlocked.Increment(ref notices);
        var alert = Alert(Sales.Id);
        chat.OnAlertsRaised(Sales.Id, Started(alert));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, notices);
        chat.OnAlertsRaised(Sales.Id, Started(Alert(Sales.Id, HealthIndicator.Blocking)));

        chat.BeginExit();
        var before = chat.Status(channel.Id);
        chat.Shutdown(TimeSpan.FromSeconds(5));

        Assert.Equal(1, notices);
        Assert.Equal(before, chat.Status(channel.Id));
    }

    [Fact]
    public void AtExit_WithNothingWatchedOrWaiting_NothingIsSent()
    {
        var sender = new FakeSender(() => Ok);
        using var chat = Create(sender, Channel());

        chat.BeginExit();
        chat.OnAlertsRaised(Sales.Id, [StoppedEnd(Alert(Sales.Id))]);
        chat.Shutdown(TimeSpan.FromSeconds(5));

        Assert.Empty(sender.Sent);
    }

    // Never answers until SqlVitals gives up the request.
    private sealed class HangingSender : IWebhookSender
    {
        public async Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return Ok;
        }

        public Task<WebhookResult> PingAsync(Uri url, CancellationToken cancel = default) => SendAsync(url, "", ChatService.Slack, cancel);
    }

    // ── Heartbeat (#170) ──────────────────────────────────────────────────────

    private const string PingUrl = "https://hc-ping.com/0f3b8a5e-1c2d-4e5f-8a9b-0c1d2e3f4a5b";

    // A clock that only moves when told, so each heartbeat check runs inside Advance, on this thread.
    private static FakeTimeProvider Clock(int hour = 8)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, hour, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    private static ChatChannel Beating(HeartbeatSchedule schedule, string? ping = null, string name = "DBA team", string url = SlackUrl)
    {
        var channel = Channel(name, url: url);
        channel.HeartbeatEvery   = schedule.Every;
        channel.HeartbeatHours   = schedule.Hours;
        channel.HeartbeatAt      = schedule.At;
        channel.HeartbeatPingUrl = ping ?? string.Empty;
        return channel;
    }

    private ChatNotificationService Beat(FakeSender sender, FakeTimeProvider time, params ChatChannel[] channels)
    {
        var service = new ChatNotificationService(sender, "DBA-PC01", _log.Enqueue, time: time);
        service.Apply(Store(channels));
        return service;
    }

    [Fact]
    public async Task ADailyHeartbeat_IsPostedAtItsTime_SayingWhatsWatched_AndPings()
    {
        var time    = Clock(hour: 8);
        var sender  = new FakeSender(() => Ok);
        var channel = Beating(HeartbeatSchedule.DailyAt(TimeSpan.FromHours(9)), PingUrl);
        using var chat = Beat(sender, time, channel);
        chat.OnWatchingChanged(Start(Sales, Hr));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), chat.NextHeartbeat(channel.Id));

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Single(sender.Sent); // not yet: only "watching"
        time.Advance(TimeSpan.FromMinutes(1));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => !sender.Pinged.IsEmpty);

        Assert.Equal("💓 SqlVitals heartbeat: watching 2 servers", sender.TitlesTo(SlackUrl).Last());
        Assert.Equal([new Uri(PingUrl)], sender.Pinged);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero), chat.NextHeartbeat(channel.Id));
        Assert.All(_log, line => Assert.DoesNotContain("hc-ping", line));
        await WaitUntil(() => _log.Any(l => l.Contains("heartbeat ping answered (200)")));
    }

    [Fact]
    public async Task AHeartbeatWatchingNothing_Warns_AndDoesntPing()
    {
        var time    = Clock();
        var sender  = new FakeSender(() => Ok);
        var channel = Beating(HeartbeatSchedule.EveryHours(1), PingUrl);
        using var chat = Beat(sender, time, channel);

        time.Advance(TimeSpan.FromHours(1));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["💓 SqlVitals heartbeat: watching no servers"], sender.TitlesTo(SlackUrl));
        Assert.Empty(sender.Pinged);
    }

    [Fact]
    public async Task AHeartbeat_NamesOnlyTheServersTheChannelTakes()
    {
        var time    = Clock();
        var sender  = new FakeSender(() => Ok);
        var channel = Beating(HeartbeatSchedule.EveryHours(1));
        channel.AllConnections = false;
        channel.ConnectionIds  = [Hr.Id];
        using var chat = Beat(sender, time, channel);
        chat.OnWatchingChanged(Start(Sales, Hr));
        // Sent before the clock moves: the queue's pacing waits on it.
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromHours(1));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("💓 SqlVitals heartbeat: watching HR-DB", sender.TitlesTo(SlackUrl).Last());
    }

    [Fact]
    public async Task HeartbeatsMissedWhileThePcSlept_AreSentOnce_ThenTheScheduleCarriesOn()
    {
        var time    = Clock(hour: 8);
        var sender  = new FakeSender(() => Ok);
        var channel = Beating(HeartbeatSchedule.EveryHours(6));
        using var chat = Beat(sender, time, channel);

        // A sleeping PC's timers don't fire: the clock jumps, and the first check finds them due.
        time.AdjustTime(time.GetUtcNow().AddDays(2).AddHours(1));
        time.Advance(ChatNotificationService.HeartbeatCheckEvery);
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(sender.TitlesTo(SlackUrl));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero), chat.NextHeartbeat(channel.Id));
    }

    [Fact]
    public async Task ChangingTheSchedule_StartsItAgain_AndTurningItOffOrTheChannelOff_StopsIt()
    {
        var time    = Clock(hour: 8);
        var sender  = new FakeSender(() => Ok);
        var channel = Beating(HeartbeatSchedule.DailyAt(TimeSpan.FromHours(9)));
        using var chat = Beat(sender, time, channel);

        var hourly = Beating(HeartbeatSchedule.EveryHours(1));
        hourly.Id = channel.Id;
        chat.Apply(Store(hourly));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), chat.NextHeartbeat(channel.Id));

        hourly.Enabled = false;
        chat.Apply(Store(hourly));
        Assert.Null(chat.NextHeartbeat(channel.Id));

        chat.Apply(Store(Channel()));
        time.Advance(TimeSpan.FromDays(1));
        await chat.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task AFailedPing_IsLogged_WithoutItsUrl()
    {
        var time    = Clock();
        var sender  = new FakeSender(url => url.Host == "hc-ping.com" ? NotFound : Ok);
        using var chat = Beat(sender, time, Beating(HeartbeatSchedule.EveryHours(1), PingUrl));
        chat.OnWatchingChanged(Start(Sales));

        time.Advance(TimeSpan.FromHours(1));
        await WaitUntil(() => _log.Any(l => l.Contains("heartbeat ping failed")));

        Assert.All(_log, line => Assert.DoesNotContain("0f3b8a5e", line));
    }

    [Fact]
    public async Task AtExit_NoMoreHeartbeats()
    {
        var time   = Clock();
        var sender = new FakeSender(() => Ok);
        var chat   = Beat(sender, time, Beating(HeartbeatSchedule.EveryHours(1), PingUrl));

        chat.BeginExit();
        chat.Shutdown(TimeSpan.FromSeconds(1));
        chat.Dispose();
        time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(50);

        Assert.Empty(sender.Sent);
        Assert.Empty(sender.Pinged);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting.");
            await Task.Delay(10);
        }
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
