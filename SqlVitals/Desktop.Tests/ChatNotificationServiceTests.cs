using System.Text.Json.Nodes;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.Tests;

/// <summary>The chat service's test message and status (#120), over a fake sender.</summary>
public sealed class ChatNotificationServiceTests
{
    private const string SlackUrl = "https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa";
    private const string TeamsUrl = "https://prod-27.westeurope.logic.azure.com:443/workflows/0a1b/triggers/manual/paths/invoke?sig=AbCdQk3a";

    private sealed class FakeSender(Func<WebhookResult> answer) : IWebhookSender
    {
        public readonly List<(Uri Url, string Json, ChatService Service)> Sent = new();

        public Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
        {
            Sent.Add((url, json, service));
            return Task.FromResult(answer());
        }
    }

    private static readonly WebhookResult Ok       = new(WebhookOutcome.Posted, 200, null, TimeSpan.FromMilliseconds(120));
    private static readonly WebhookResult NotFound = new(WebhookOutcome.NotFound, 404, "no_service", TimeSpan.FromMilliseconds(80));

    private readonly List<string> _log = new();

    private ChatNotificationService Create(FakeSender sender, params ChatChannel[] channels)
    {
        var service = new ChatNotificationService(sender, "DBA-PC01", _log.Add);
        service.Apply(new ConnectionStore { ChatChannels = channels.ToList() });
        return service;
    }

    private static ChatChannel Channel(string name = "DBA team", ChatService service = ChatService.Slack, string url = SlackUrl) =>
        new() { Name = name, Service = service, WebhookUrl = url };

    [Fact]
    public async Task ATestMessage_IsPostedInTheChannelsFormat_AndMarksItPosted()
    {
        var sender  = new FakeSender(() => Ok);
        var channel = Channel(service: ChatService.Teams, url: TeamsUrl);
        var chat    = Create(sender, channel);
        var changed = new List<Guid>();
        chat.StatusChanged += changed.Add;

        var result = await chat.SendTestAsync(channel.Id);

        Assert.True(result.IsSuccess);
        var (url, json, service) = Assert.Single(sender.Sent);
        Assert.Equal((TeamsUrl, ChatService.Teams), (url.OriginalString, service));
        Assert.Equal("message", (string?)JsonNode.Parse(json)!["type"]);
        Assert.Contains("Test message from SqlVitals on DBA-PC01. Alerts will be posted here.", json);
        Assert.NotNull(chat.Status(channel.Id).LastPostedUtc);
        Assert.Equal([channel.Id], changed);
    }

    [Fact]
    public async Task AFailedTest_LeavesTheStatusAlone()
    {
        var channel = Channel();
        var chat    = Create(new FakeSender(() => NotFound), channel);

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

        Assert.True((await Create(sender, channel).SendTestAsync(channel.Id)).IsSuccess);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task EveryPostAndFailure_IsLoggedByName_NeverWithTheUrl()
    {
        var good = Channel("Good one");
        var bad  = Channel("Bad one", url: SlackUrl + "X");
        var answers = new Queue<WebhookResult>([Ok, NotFound]);
        var chat = Create(new FakeSender(answers.Dequeue), good, bad);

        await chat.SendTestAsync(good.Id);
        await chat.SendTestAsync(bad.Id);

        Assert.Collection(_log,
            l => Assert.Equal("Chat \"Good one\" (Slack): test message posted (200) in 120 ms", l),
            l => Assert.Equal("Chat \"Bad one\" (Slack): test message webhook not found (404): no_service", l));
        Assert.All(_log, l => Assert.DoesNotContain("hooks.slack.com", l));
    }

    [Fact]
    public async Task ASenderThatThrows_IsReportedAsAFailure_NotAnException()
    {
        var channel = Channel();
        var chat = Create(new FakeSender(() => throw new InvalidOperationException($"bug posting to {SlackUrl}")), channel);

        var result = await chat.SendTestAsync(channel.Id);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("hooks.slack.com", result.Describe(ChatService.Slack));
        Assert.All(_log, l => Assert.DoesNotContain("hooks.slack.com", l));
    }

    [Fact]
    public async Task AStoreThatCouldntBeRead_IsIgnored_SoTheChannelsStay()
    {
        var channel = Channel();
        var chat = Create(new FakeSender(() => Ok), channel);
        await chat.SendTestAsync(channel.Id);

        chat.Apply(new ConnectionStore { ReadFailed = true });

        Assert.Single(chat.Channels);
        Assert.NotNull(chat.Status(channel.Id).LastPostedUtc);
    }

    [Fact]
    public async Task ARemovedChannel_IsForgotten_WithItsStatus()
    {
        var channel = Channel();
        var chat = Create(new FakeSender(() => Ok), channel);
        await chat.SendTestAsync(channel.Id);

        chat.Apply(new ConnectionStore());

        Assert.Empty(chat.Channels);
        Assert.Equal(ChannelStatus.NeverUsed, chat.Status(channel.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendTestAsync(channel.Id));
    }

    [Fact]
    public async Task AnEditApplies_ToTheNextMessage()
    {
        var channel = Channel();
        var sender  = new FakeSender(() => Ok);
        var chat    = Create(sender, channel);

        var edited = channel.Clone();
        edited.Service    = ChatService.Teams;
        edited.WebhookUrl = TeamsUrl;
        chat.Apply(new ConnectionStore { ChatChannels = [edited] });
        await chat.SendTestAsync(channel.Id);

        Assert.Equal((TeamsUrl, ChatService.Teams), (sender.Sent[0].Url.OriginalString, sender.Sent[0].Service));
    }
}
