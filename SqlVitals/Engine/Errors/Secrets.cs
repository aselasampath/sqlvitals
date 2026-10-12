using System.Text.RegularExpressions;

namespace SqlVitals.Engine.Errors;

/// <summary>
/// Removes secrets from text before it leaves SqlVitals, in case a driver, a parser or a web
/// service echoes one back: passwords and access tokens, bearer tokens and chat webhook URLs.
/// <see cref="Redact"/> is for what is shown or exported (error messages and dialogs, the activity
/// log, Export for AI); <see cref="ForLog"/> also removes whole connection strings, for the log
/// files. Shared by the app and the collector service (#172), so both leave out the same things.
/// </summary>
public static partial class Secrets
{
    /// <summary>What a masked value is replaced with: "Password=*****".</summary>
    public const string Mask = "*****";

    /// <summary>
    /// Masks password and access token values ("Password=*****", also in SQL such as
    /// <c>WITH PASSWORD = '…'</c>), bearer tokens and JSON web tokens, and replaces chat webhook
    /// URLs (#175). The rest of the text is kept, so a message still says what went wrong.
    /// </summary>
    public static string Redact(string? message)
    {
        var text = message ?? string.Empty;
        text = PasswordPattern().Replace(text, "$1$2" + Mask);
        text = TokenFieldPattern().Replace(text, "$1" + Mask);
        text = BearerPattern().Replace(text, "$1" + Mask);
        text = JwtPattern().Replace(text, Mask);
        return WebhookUrlPattern().Replace(text, "[webhook URL removed]");
    }

    /// <summary>
    /// <see cref="Redact"/>, and anything that looks like a connection string is replaced, so none
    /// of them reaches a log file even when an exception message echoes one back.
    /// </summary>
    public static string ForLog(string? message) =>
        ConnectionStringPattern().Replace(Redact(message), "[connection string removed] ");

    // A connection string's Password= / Pwd= / Access Token=, and SQL's PASSWORD = '…' or N'…'.
    // A quoted value may hold its quote doubled ('it''s'); one cut off before its closing quote
    // (a query shortened for a report) runs to the line's end, never across lines. An unquoted
    // value runs to the ";" or the line's end: a password may have spaces in it.
    [GeneratedRegex(@"(?i)\b(password|pwd|access[ _]?token)(\s*=\s*)(N?'(?:[^'\r\n]|'')*(?:'|(?=[\r\n])|$)|""(?:[^""\r\n]|"""")*(?:""|(?=[\r\n])|$)|[^;""'\r\n]*)")]
    private static partial Regex PasswordPattern();

    // OAuth answers and query strings: "access_token": "…", refresh_token=…, id_token=….
    [GeneratedRegex(@"(?i)(\b(?:access|refresh|id)_token""?\s*[:=]\s*""?)([^""&\s,;}]+)")]
    private static partial Regex TokenFieldPattern();

    // An Authorization header's value. A token is long; "bearer of bad news" is left alone.
    [GeneratedRegex(@"(?i)(\bbearer\s+)[A-Za-z0-9\-._~+/]{16,}=*")]
    private static partial Regex BearerPattern();

    // A JSON web token (Entra ID, most OAuth providers), wherever it turns up: header.payload.signature.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JwtPattern();

    // Two or more "key=value;" pairs with a connection-string keyword — a whole connection
    // string (or most of one) echoed back by a driver or parser error.
    [GeneratedRegex(
        @"(?i)(?:\b(?:server|data\s+source|addr|address|network\s+address|initial\s+catalog|database|" +
        @"user\s+id|uid|user|password|pwd|integrated\s+security|trusted_connection|authentication|" +
        @"encrypt|trustservercertificate|trust\s+server\s+certificate|connect(?:ion)?\s+timeout|" +
        @"application\s+name|multipleactiveresultsets|persist\s+security\s+info)\s*=\s*" +
        @"(?:""[^""]*""|'[^']*'|[^;\r\n]*)(?:;[ \t]*|(?=[\r\n])|$)){2,}")]
    private static partial Regex ConnectionStringPattern();

    // A Slack or Teams webhook URL (#120): anyone who has one can post to the channel. SqlVitals
    // never shows or logs one on purpose; this catches one echoed back in an exception or a service's answer.
    [GeneratedRegex(
        @"(?i)\bhttps?://[^\s""'<>]*(?:hooks\.slack\.com|logic\.azure\.com|powerplatform\.com|webhook\.office\.com|outlook\.office\.com/webhook)[^\s""'<>]*")]
    private static partial Regex WebhookUrlPattern();
}
