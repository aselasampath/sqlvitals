using System.Text.RegularExpressions;

namespace SqlVitals.Engine.Errors;

/// <summary>
/// Removes secrets from text before it is shown or logged, in case a driver, a parser or a web
/// service echoes one back: passwords, connection strings and chat webhook URLs. Shared by the
/// app and the collector service (#172), so both logs leave out the same things.
/// </summary>
public static partial class Secrets
{
    /// <summary>Masks password values: "Password=*****".</summary>
    public static string MaskPasswords(string? message) =>
        PasswordPattern().Replace(message ?? string.Empty, "$1$2*****");

    /// <summary>
    /// Masks passwords, then replaces anything that looks like a connection string or a chat
    /// webhook URL, so none of them reaches a log even when an exception message echoes one back.
    /// </summary>
    public static string ForLog(string? message) =>
        WebhookUrlPattern().Replace(
            ConnectionStringPattern().Replace(MaskPasswords(message), "[connection string removed] "),
            "[webhook URL removed]");

    [GeneratedRegex(@"(?i)\b(password|pwd)(\s*=\s*)(""[^""]*""|'[^']*'|[^;""']*)")]
    private static partial Regex PasswordPattern();

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
    // never logs one on purpose; this catches one echoed back in an exception or a service's answer.
    [GeneratedRegex(
        @"(?i)\bhttps?://[^\s""'<>]*(?:hooks\.slack\.com|logic\.azure\.com|powerplatform\.com|webhook\.office\.com|outlook\.office\.com/webhook)[^\s""'<>]*")]
    private static partial Regex WebhookUrlPattern();
}
