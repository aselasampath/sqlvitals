using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

public sealed class WebhookUrlTests
{
    internal const string Slack = "https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa";
    internal const string Teams = "https://prod-27.westeurope.logic.azure.com:443/workflows/0a1b2c/triggers/manual/paths/invoke?api-version=2016-06-01&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=AbCdEfQk3a";
    internal const string TeamsPowerPlatform = "https://default0a1b.2c.environment.api.powerplatform.com:443/powerautomate/automations/direct/workflows/0a1b/triggers/manual/paths/invoke?api-version=1&sig=Zz9Yy8";

    private static WebhookUrlCheck Check(string url, ChatService service, params ChatChannel[] others) =>
        WebhookUrl.Check(url, service, others, Guid.NewGuid());

    [Fact]
    public void ASlackIncomingWebhook_IsValid()
    {
        var check = Check(Slack, ChatService.Slack);

        Assert.True(check.IsValid);
        Assert.Null(check.Warning);
        Assert.Equal(ChatService.Slack, check.Detected);
    }

    [Theory]
    [InlineData(Teams)]
    [InlineData(TeamsPowerPlatform)]
    public void ATeamsWorkflowWebhook_IsValid(string url)
    {
        var check = Check(url, ChatService.Teams);

        Assert.True(check.IsValid);
        Assert.Null(check.Warning);
        Assert.Equal(ChatService.Teams, check.Detected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hooks.slack.com/services/T/B/x")]
    [InlineData("not a url")]
    public void AnEmptyOrPartialUrl_IsRefused(string url)
    {
        Assert.False(Check(url, ChatService.Slack).IsValid);
    }

    [Fact]
    public void Http_IsRefused()
    {
        var check = Check("http://hooks.slack.com/services/T/B/x", ChatService.Slack);

        Assert.False(check.IsValid);
        Assert.Contains("https://", check.Error);
    }

    [Theory]
    [InlineData("https://contoso.webhook.office.com/webhookb2/abc@def/IncomingWebhook/123/456")]
    [InlineData("https://outlook.office.com/webhook/abc@def/IncomingWebhook/123/456")]
    public void AnOffice365ConnectorUrl_IsRefused_WithTheWikiLink(string url)
    {
        var check = Check(url, ChatService.Teams);

        Assert.False(check.IsValid);
        Assert.Contains("Office 365 connector", check.Error);
        Assert.Contains(WebhookUrl.TeamsSetupLink, check.Error);
    }

    [Fact]
    public void ASlackWorkflowBuilderLink_IsRefused()
    {
        var check = Check("https://hooks.slack.com/triggers/T0001/123/abc", ChatService.Slack);

        Assert.False(check.IsValid);
        Assert.Contains("Workflow Builder", check.Error);
    }

    [Fact]
    public void ASlackUrl_NeedsSlackAsTheService()
    {
        var check = Check(Slack, ChatService.Teams);

        Assert.False(check.IsValid);
        Assert.Equal(ChatService.Slack, check.Detected);
    }

    [Fact]
    public void SlackAsTheService_NeedsASlackHost()
    {
        Assert.False(Check("https://example.com/hook", ChatService.Slack).IsValid);
    }

    [Fact]
    public void AnUnknownTeamsHost_IsAllowedWithAWarning()
    {
        var check = Check("https://workflows.example.net/invoke?sig=abc", ChatService.Teams);

        Assert.True(check.IsValid);
        Assert.NotNull(check.Warning);
        Assert.Null(check.Detected);
    }

    [Theory]
    [InlineData(Slack)]
    [InlineData("https://HOOKS.Slack.COM/services/T0001/B0002/abcdEFGHijklMNOPx7Qa")]
    [InlineData("https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa/")]
    [InlineData("https://hooks.slack.com:443/services/T0001/B0002/abcdEFGHijklMNOPx7Qa")]
    [InlineData("  https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa  ")]
    public void AUrlAlreadyUsedByAnotherChannel_IsRefused_NamingIt(string url)
    {
        var other = new ChatChannel { Name = "DBA team", WebhookUrl = Slack };

        var check = Check(url, ChatService.Slack, other);

        Assert.False(check.IsValid);
        Assert.Contains("\"DBA team\"", check.Error);
    }

    [Fact]
    public void TheChannelBeingEdited_IsntADuplicateOfItself()
    {
        var self = new ChatChannel { Name = "DBA team", WebhookUrl = Slack };

        Assert.True(WebhookUrl.Check(Slack, ChatService.Slack, [self], self.Id).IsValid);
    }

    [Fact]
    public void ThePath_IsCaseSensitive_SoADifferentTokenIsntADuplicate()
    {
        var other = new ChatChannel { Name = "DBA team", WebhookUrl = Slack };

        Assert.True(Check(Slack.Replace("x7Qa", "x7QA"), ChatService.Slack, other).IsValid);
    }

    [Theory]
    [InlineData(Slack, "hooks.slack.com/…/x7Qa")]
    [InlineData(Teams, "prod-27.westeurope.logic.azure.com/…/Qk3a")]
    [InlineData("", "")]
    [InlineData("nonsense", "…")]
    public void Mask_ShowsTheHostAndTheLastFourCharacters(string url, string expected)
    {
        Assert.Equal(expected, WebhookUrl.Mask(url));
    }

    [Fact]
    public void Mask_NeverContainsTheSecretPart()
    {
        Assert.DoesNotContain("abcdEFGH", WebhookUrl.Mask(Slack));
        Assert.DoesNotContain("sig=", WebhookUrl.Mask(Teams));
    }

    [Fact]
    public void AChannel_ToString_ShowsItsMaskNotItsUrl()
    {
        var text = new ChatChannel { Name = "DBA team", WebhookUrl = Slack }.ToString();

        Assert.Contains("DBA team", text);
        Assert.DoesNotContain("services/T0001", text);
    }
}
