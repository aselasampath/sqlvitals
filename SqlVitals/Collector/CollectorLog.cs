using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Collector;

/// <summary>
/// The collector's log lines on any <see cref="ILogger"/>, as the app writes its own: the text as
/// written, not a message template, so braces in an error's text stay as they are. Every line,
/// and an error's exception, goes through <see cref="Secrets.ForLog"/> here, before any provider
/// sees it: the Event Log and the console as well as the file.
/// </summary>
internal static class PlainLog
{
    /// <summary>An error, with its exception folded into the text. A cancelled read isn't one: it is left out.</summary>
    public static void Error(this ILogger log, string message, Exception ex)
    {
        if (ex is OperationCanceledException)
            return;
        Write(log, LogLevel.Error, $"{message}{Environment.NewLine}{ex}");
    }

    public static void Warning(this ILogger log, string message) => Write(log, LogLevel.Warning, message);

    public static void Info(this ILogger log, string message) => Write(log, LogLevel.Information, message);

    // Masked only when someone will read it.
    private static void Write(ILogger log, LogLevel level, string message)
    {
        if (log.IsEnabled(level))
            log.Log(level, default, Secrets.ForLog(message), null, Text);
    }

    private static string Text(string message, Exception? _) => message;
}

/// <summary>
/// The service's log files (#172), one a day in the data folder's logs\, kept
/// <see cref="RetentionDays"/> days, every line through <see cref="Secrets.ForLog"/>: the same
/// shape as the app's log, so one reading habit covers both. Warnings and errors also go to the
/// Windows Event Log while it runs as a service.
/// </summary>
internal sealed class CollectorFileLoggerProvider(string directory) : ILoggerProvider
{
    public const int RetentionDays = 14;

    private const string FilePrefix = "Collector-";

    private readonly object _gate = new();

    public string Directory { get; } = directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this);

    /// <summary>Deletes log files older than <see cref="RetentionDays"/>. Called once at start.</summary>
    public void DeleteExpiredFiles(DateTime today)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, $"{FilePrefix}*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                    day < today.AddDays(-RetentionDays))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retried at the next start.
        }
    }

    internal void Write(LogLevel level, string message)
    {
        try
        {
            var now  = DateTime.Now;
            var file = Path.Combine(Directory, $"{FilePrefix}{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
            var line = $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {Name(level)} {Secrets.ForLog(message)}{Environment.NewLine}";
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the service.
        }
    }

    private static string Name(LogLevel level) => level switch
    {
        LogLevel.Information => "INFO",
        LogLevel.Warning     => "WARN",
        LogLevel.Error       => "ERROR",
        LogLevel.Critical    => "CRITICAL",
        _                    => level.ToString().ToUpperInvariant(),
    };

    public void Dispose()
    {
        // Each line is appended and closed: nothing is held open.
    }

    private sealed class FileLogger(CollectorFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var message = formatter(state, exception);
            provider.Write(logLevel, exception is null ? message : $"{message}{Environment.NewLine}{exception}");
        }
    }
}
