using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>The app log's last line of defence: a webhook URL echoed back never reaches the file (#120).</summary>
public sealed class AppLogRedactTests
{
    [Theory]
    [InlineData("https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOP")]
    [InlineData("https://prod-27.westeurope.logic.azure.com:443/workflows/0a1b/triggers/manual/paths/invoke?api-version=2016-06-01&sig=AbCd")]
    [InlineData("https://default0a1b.2c.environment.api.powerplatform.com:443/powerautomate/automations/direct/workflows/0a1b/triggers/manual/paths/invoke?sig=Zz")]
    [InlineData("https://contoso.webhook.office.com/webhookb2/abc@def/IncomingWebhook/123/456")]
    [InlineData("https://outlook.office.com/webhook/abc@def/IncomingWebhook/123/456")]
    public void AWebhookUrl_IsRemoved(string url)
    {
        var redacted = AppLog.Redact($"Posting to {url} failed: 404 (\"{url}\")");

        Assert.DoesNotContain(url, redacted);
        Assert.Contains("[webhook URL removed]", redacted);
        Assert.StartsWith("Posting to [webhook URL removed] failed: 404", redacted);
    }

    [Fact]
    public void OtherLinks_AndText_AreLeftAlone()
    {
        const string text = "See https://learn.microsoft.com/sql for details.";

        Assert.Equal(text, AppLog.Redact(text));
    }

    [Fact]
    public void ConnectionStrings_AreStillRemoved()
    {
        var redacted = AppLog.Redact("Failed: Server=sql01;User ID=sa;Password=s3cret;");

        Assert.DoesNotContain("s3cret", redacted);
    }
}
