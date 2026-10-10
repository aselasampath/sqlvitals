using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Plain-text application log, one file per day under %APPDATA%\SqlVitals\logs, kept for
/// <see cref="RetentionDays"/> days so it can be attached to a bug report. Nothing secret is
/// written: see <see cref="Redact"/>. Since #166 every <c>ILogger&lt;T&gt;</c> of the app's writes
/// here (<see cref="FileLoggerProvider"/>); <see cref="AppLog"/> is the static way in.
/// </summary>
public static class FileLog
{
    public const int RetentionDays = 14;

    private const string FilePrefix = "SqlVitals-";

    private static readonly object Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlVitals", "logs");

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
    /// Masks passwords, then replaces anything that looks like a connection string or a chat
    /// webhook URL, so none of them reaches the log even when an exception message echoes one back.
    /// </summary>
    public static string Redact(string message) => Secrets.ForLog(message);

    /// <summary>The level as a line of the file names it: "INFO", "ERROR".</summary>
    internal static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace       => "TRACE",
        LogLevel.Debug       => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning     => "WARN",
        LogLevel.Error       => "ERROR",
        LogLevel.Critical    => "CRITICAL",
        _                    => level.ToString().ToUpperInvariant(),
    };

    internal static void Write(string level, string message)
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

/// <summary>
/// Every <c>ILogger</c> of the app's, on <see cref="FileLog"/> (#166): a line per entry as before,
/// "12:04:31.207 ERROR message", the exception on the lines after it.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => FileLogger.Instance;

    public void Dispose()
    {
        // Each line is appended and closed: nothing is held open.
    }

    private sealed class FileLogger : ILogger
    {
        public static readonly FileLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            FileLog.Write(FileLog.LevelName(logLevel),
                          exception is null ? message : $"{message}{Environment.NewLine}{exception}");
        }
    }
}
