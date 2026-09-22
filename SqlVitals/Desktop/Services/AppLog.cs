using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Plain-text application log, one file per day under %APPDATA%\SqlVitals\logs, kept for
/// <see cref="RetentionDays"/> days so it can be attached to a bug report. Nothing secret is
/// written: see <see cref="Redact"/>.
/// </summary>
public static class AppLog
{
    public const int RetentionDays = 14;

    private const string FilePrefix = "SqlVitals-";

    private static readonly object Gate = new();

    // Two or more "key=value;" pairs with a connection-string keyword — a whole connection
    // string (or most of one) echoed back by a driver or parser error.
    private static readonly Regex ConnectionString = new(
        @"(?i)(?:\b(?:server|data\s+source|addr|address|network\s+address|initial\s+catalog|database|" +
        @"user\s+id|uid|user|password|pwd|integrated\s+security|trusted_connection|authentication|" +
        @"encrypt|trustservercertificate|trust\s+server\s+certificate|connect(?:ion)?\s+timeout|" +
        @"application\s+name|multipleactiveresultsets|persist\s+security\s+info)\s*=\s*" +
        @"(?:""[^""]*""|'[^']*'|[^;\r\n]*)(?:;[ \t]*|(?=[\r\n])|$)){2,}",
        RegexOptions.Compiled);

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlVitals", "logs");

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    /// <summary>Deletes log files older than <see cref="RetentionDays"/>. Called once at startup.</summary>
    public static void DeleteExpiredFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return;

            var cutoff = DateTime.Today.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, $"{FilePrefix}*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && day < cutoff)
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open elsewhere is retried on the next start.
        }
    }

    /// <summary>
    /// Masks passwords, then replaces anything that looks like a connection string, so neither
    /// reaches the log even when an exception message echoes one back.
    /// </summary>
    public static string Redact(string message) =>
        ConnectionString.Replace(ConnectionSettingsService.RedactSecrets(message), "[connection string removed] ");

    private static void Write(string level, string message)
    {
        try
        {
            var now  = DateTime.Now;
            var file = Path.Combine(Directory, $"{FilePrefix}{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
            var line = $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} " +
                       $"{Redact(message)}{Environment.NewLine}";

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}
