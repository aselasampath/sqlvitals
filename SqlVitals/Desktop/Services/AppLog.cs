using Microsoft.Extensions.Logging;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// The log for code the container doesn't make (#166): pages, controls, helpers and startup. A
/// thin adapter over the same <see cref="ILoggerFactory"/> the container's <c>ILogger&lt;T&gt;</c>
/// come from, so every entry lands in <see cref="FileLog"/>. A service the container makes asks
/// for an <c>ILogger&lt;T&gt;</c> instead.
/// </summary>
public static class AppLog
{
    /// <summary>The app's loggers: one file, information and up.</summary>
    public static ILoggerFactory Factory { get; } = LoggerFactory.Create(logging => logging.AddProvider(new FileLoggerProvider()));

    private static readonly ILogger Log = Factory.CreateLogger("SqlVitals");

    /// <summary>A logger for a type the container doesn't make, or for a test that passes none.</summary>
    public static ILogger<T> For<T>() => Factory.CreateLogger<T>();

    public static void Error(string message, Exception ex) => Log.Error(message, ex);

    public static void Info(string message) => Log.Info(message);
}

/// <summary>
/// The app's log lines on any <see cref="ILogger"/>: the text as written, not a message template,
/// so braces in an error's text (a query, JSON) stay as they are.
/// </summary>
public static class PlainLog
{
    public static void Error(this ILogger log, string message, Exception ex) =>
        log.Log(LogLevel.Error, default, message, ex, Text);

    public static void Info(this ILogger log, string message) =>
        log.Log(LogLevel.Information, default, message, null, Text);

    private static string Text(string message, Exception? _) => message;
}
