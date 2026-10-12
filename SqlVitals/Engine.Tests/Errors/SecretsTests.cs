using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Tests.Errors;

/// <summary>What leaves SqlVitals carries no secret (#175): each pattern, and the text around it kept.</summary>
public sealed class SecretsTests
{
    [Theory]
    [InlineData("Login failed: Server=sql01;User ID=sa;Password=s3cret;", "s3cret", "Password=*****;")]
    [InlineData("Server=sql01;Pwd=\"a;b c\";Encrypt=True", "a;b c", "Pwd=*****;")]
    [InlineData("Server=sql01;Password=my long pass phrase;Encrypt=True", "long pass", "Password=*****;")]
    [InlineData("Server=tcp:x.database.windows.net;Access Token=abc.def.ghi;", "abc.def.ghi", "Access Token=*****;")]
    [InlineData("CREATE LOGIN app WITH PASSWORD = 'Pa55word!', CHECK_POLICY = OFF", "Pa55word!", "PASSWORD = *****,")]
    [InlineData("CREATE LOGIN app WITH PASSWORD = N'S3cret!' MUST_CHANGE", "S3cret!", "PASSWORD = ***** MUST_CHANGE")]
    [InlineData("ALTER LOGIN app WITH PASSWORD = 'it''s mine', CHECK_POLICY = OFF", "s mine", "PASSWORD = *****,")]
    public void PasswordsAndAccessTokens_AreMasked(string text, string secret, string masked)
    {
        var redacted = Secrets.Redact(text);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(masked, redacted);
    }

    [Fact]
    public void AValueCutOffBeforeItsQuote_IsMaskedToTheLineEnd_AndTheLinesAfterAreKept()
    {
        // Export for AI shortens each query to 60–80 characters; the cut can fall inside the password.
        const string report = "  ALTER LOGIN app WITH PASSWORD = 'Pa55w\r\n  Next row: Name = 'Ann'\r\nSECTION 2";

        Assert.Equal("  ALTER LOGIN app WITH PASSWORD = *****\r\n  Next row: Name = 'Ann'\r\nSECTION 2", Secrets.Redact(report));
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOi.abcDEF123_-ghi.sig", "Authorization: Bearer *****")]
    [InlineData("401 for bearer AbCdEfGhIjKlMnOpQrStUvWx== on GET", "401 for bearer ***** on GET")]
    public void BearerTokens_AreMasked(string text, string expected) =>
        Assert.Equal(expected, Secrets.Redact(text));

    [Fact]
    public void ShortWordsAfterBearer_AreLeftAlone()
    {
        const string text = "The bearer token expired.";

        Assert.Equal(text, Secrets.Redact(text));
    }

    [Theory]
    [InlineData("{\"access_token\":\"opaque-token-value\",\"expires_in\":3599}", "opaque-token-value")]
    [InlineData("redirect?code=1&refresh_token=0.AAAAbbbb&state=2", "0.AAAAbbbb")]
    [InlineData("token: eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4", "eyJzdWIiOiIxMjM0NTY3ODkwIn0")]
    public void TokenFieldsAndJsonWebTokens_AreMasked(string text, string secret)
    {
        var redacted = Secrets.Redact(text);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(Secrets.Mask, redacted);
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOP")]
    [InlineData("https://contoso.webhook.office.com/webhookb2/abc@def/IncomingWebhook/123/456")]
    [InlineData("https://prod-27.westeurope.logic.azure.com:443/workflows/0a1b/triggers/manual/paths/invoke?sig=AbCd")]
    public void WebhookUrls_AreRemoved_FromWhatIsShownToo(string url)
    {
        var redacted = Secrets.Redact($"Posting to {url} failed: 404");

        Assert.Equal("Posting to [webhook URL removed] failed: 404", redacted);
    }

    [Fact]
    public void OrdinaryText_IsLeftAlone()
    {
        const string text = "Login failed for user 'sa'. See https://learn.microsoft.com/sql (error 18456, state 8).";

        Assert.Equal(text, Secrets.Redact(text));
        Assert.Equal(text, Secrets.ForLog(text));
    }

    [Fact]
    public void ForLog_AlsoRemovesTheWholeConnectionString()
    {
        var logged = Secrets.ForLog("Failed: Server=sql01;User ID=sa;Password=s3cret;");

        Assert.Equal("Failed: [connection string removed] ", logged);
    }

    [Fact]
    public void Nothing_IsNothing()
    {
        Assert.Equal(string.Empty, Secrets.Redact(null));
        Assert.Equal(string.Empty, Secrets.ForLog(null));
    }
}
